﻿namespace SwaggerProvider.Internal.Compilers

open ProviderImplementation.ProvidedTypes
open FSharp.Data.Runtime.NameUtils
open Swagger.Parser.Schema
open SwaggerProvider.Internal

open System
open FSharp.Data

open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.ExprShape
open System.Text.RegularExpressions

/// Object for compiling operations.
type OperationCompiler (schema:SwaggerObject, defCompiler:DefinitionCompiler, ignoreControllerPrefix, ignoreOperationId) =

    let compileOperation (methodName:string) (op:OperationObject) =
        if String.IsNullOrWhiteSpace methodName
            then failwithf "Operation name could not be empty. See '%s/%A'" op.Path op.Type

        let parameters =
            [
             let required, optional = op.Parameters |> Array.partition (fun x->x.Required)
             let parameters = Array.append required optional
             for x in parameters ->
                let paramName = niceCamelName x.Name
                let paramType = defCompiler.CompileTy methodName paramName x.Type x.Required
                if x.Required then ProvidedParameter(paramName, paramType)
                else 
                    let paramDefaultValue = defCompiler.GetDefaultValue paramType
                    ProvidedParameter(paramName, paramType, false, paramDefaultValue)
            ]
        let retTy =
            let okResponse = // BUG :  wrong selector
                op.Responses |> Array.tryFind (fun (code, resp) ->
                    (code.IsSome && (code.Value = 200 || code.Value = 201)) || code.IsNone)
            match okResponse with
            | Some (_,resp) ->
                match resp.Schema with
                | None -> typeof<unit>
                | Some ty -> defCompiler.CompileTy methodName "Response" ty true
            | None -> typeof<unit>

        let m = ProvidedMethod(methodName, parameters, retTy, invokeCode = fun args ->
            let thisTy = typeof<ProvidedSwaggerBaseType>
            let this = Expr.Coerce(args.[0], thisTy)
            let host = Expr.PropertyGet(this, thisTy.GetProperty("Host"))
            let headers = Expr.PropertyGet(this, thisTy.GetProperty("Headers"))
            let customizeHttpRequest = Expr.PropertyGet(this, thisTy.GetProperty("CustomizeHttpRequest"))

            let basePath =
                let basePath = schema.BasePath
                <@ RuntimeHelpers.combineUrl (%%host : string) basePath @>

            // Fit headers into quotation
            let headers =
                let jsonConsumable = op.Consumes |> Seq.exists (fun mt -> mt="application/json")
                <@@
                    let headersArr = (%%headers:(string*string)[])
                    let ctHeaderExist = headersArr |> Array.exists (fun (h,_)->h="Content-Type")
                    if not(ctHeaderExist) && jsonConsumable
                    then Array.append [|"Content-Type","application/json"|] headersArr
                    else headersArr
                @@>
                //let headerPairs =
                //    seq {
                //        yield! headers
                //        if (headers |> Seq.exists (fun (h,_)->h="Content-Type") |> not) then
                //            if (op.Consumes |> Seq.exists (fun mt -> mt="application/json")) then
                //                yield "Content-Type","application/json"
                //    }
                //    |> List.ofSeq
                //    |> List.map (fun (h1,h2) -> Expr.NewTuple [Expr.Value(h1);Expr.Value(h2)])
                //Expr.NewArray (typeof<Tuple<string,string>>, headerPairs)

            // Locates parameters matching the arguments
            let parameters =
                List.tail args // skip `this` param
                |> List.map (function
                    | ShapeVar sVar as expr ->
                        let param =
                            op.Parameters
                            |> Array.find (fun x -> niceCamelName x.Name = sVar.Name) // ???
                        param, expr
                    | _  ->
                        failwithf "Function '%s' does not support functions as arguments." methodName
                    )


            // Makes argument a string // TODO: Make body an exception
            let coerceString defType (format : CollectionFormat) exp =
                let obj = Expr.Coerce(exp, typeof<obj>)
                <@ (%%obj : obj).ToString() @>

            let rec corceQueryString name expr =
                let obj = Expr.Coerce(expr, typeof<obj>)
                <@ let o = (%%obj : obj)
                   RuntimeHelpers.toQueryParams name o @>

            let replacePathTemplate path name (exp : Expr) =
                let template = "{" + name + "}"
                <@@ Regex.Replace(%%path, template, string (%%exp : string)) @@>

            let addPayload load (param : ParameterObject) (exp : Expr) =
                let name = param.Name
                let var = coerceString param.Type param.CollectionFormat exp
                match load with
                | Some (FormData, b) -> Some (FormData, <@@ Seq.append %%b [name, (%var : string)] @@>)
                | None               -> match param.In with
                                        | Body -> Some (Body, Expr.Coerce (exp, typeof<obj>))
                                        | _    -> Some (FormData, <@@ (seq [name, (%var : string)]) @@>)
                | _                  -> failwith ("Can only contain one payload")

            let addQuery quer name (exp : Expr) =
                let listValues = corceQueryString name exp
                <@@ List.append
                        (%%quer : (string*string) list)
                        (%listValues : (string*string) list) @@>

            let addHeader head name (exp : Expr) =
                <@@ Array.append (%%head : (string*string) []) ([|name, (%%exp : string)|]) @@>

            // Partitions arguments based on their locations
            let (path, payload, queries, heads) =
                let mPath = op.Path
                parameters
                |> List.fold (
                    fun (path, load, quer, head) (param : ParameterObject, exp) ->
                        let name = param.Name
                        let value = coerceString param.Type param.CollectionFormat exp
                        match param.In with
                        | Path   -> (replacePathTemplate path name value, load, quer, head)
                        | FormData
                        | Body   -> (path, addPayload load param exp, quer, head)
                        | Query  -> (path, load, addQuery quer name exp, head)
                        | Header -> (path, load, quer, addHeader head name value)
                    )
                    (<@@ mPath @@>, None, <@@ ([] : (string*string) list)  @@>, headers)


            let address = <@@ RuntimeHelpers.combineUrl %basePath (%%path :string ) @@>
            let restCall = op.Type.ToString()

            let customizeHttpRequest =
                <@@ let customizeCall = (%%customizeHttpRequest : Net.HttpWebRequest -> Net.HttpWebRequest)
                    fun (request:Net.HttpWebRequest) ->
                        if restCall = "Post"
                            then request.ContentLength <- 0L
                        customizeCall request @@>

            // Make HTTP call
            let result =
                match payload with
                | None ->
                    <@@ Http.RequestString(%%address,
                            httpMethod = restCall,
                            headers = (%%heads : array<string*string>),
                            query = (%%queries : (string * string) list),
                            customizeHttpRequest = (%%customizeHttpRequest : Net.HttpWebRequest -> Net.HttpWebRequest)) @@>
                | Some (FormData, b) ->
                    <@@ Http.RequestString(%%address,
                            httpMethod = restCall,
                            headers = (%%heads : array<string*string>),
                            body = HttpRequestBody.FormValues (%%b : seq<string * string>),
                            query = (%%queries : (string * string) list),
                            customizeHttpRequest = (%%customizeHttpRequest : Net.HttpWebRequest -> Net.HttpWebRequest)) @@>
                | Some (Body, b)     ->
                    <@@ let body = RuntimeHelpers.serialize (%%b : obj)
                        Http.RequestString(%%address,
                            httpMethod = restCall,
                            headers = (%%heads : array<string*string>),
                            body = HttpRequestBody.TextRequest body,
                            query = (%%queries : (string * string) list),
                            customizeHttpRequest = (%%customizeHttpRequest : Net.HttpWebRequest -> Net.HttpWebRequest))
                    @@>
                | Some (x, _) -> failwith ("Payload should not be able to have type: " + string x)

            // Return deserialized object
            let value = <@@ RuntimeHelpers.deserialize (%%result : string) retTy @@>
            Expr.Coerce(value, retTy)
        )
        if not <| String.IsNullOrEmpty(op.Summary)
            then m.AddXmlDoc(op.Summary) // TODO: Use description of parameters in docs
        if op.Deprecated
            then m.AddObsoleteAttribute("Operation is deprecated", false)
        m

    static member GetMethodNameCandidate (op:OperationObject) skipLength ignoreOperationId =
        if ignoreOperationId || String.IsNullOrWhiteSpace(op.OperationId)
        then
            [|  yield op.Type.ToString()
                yield!
                    op.Path.Split('/')
                    |> Array.filter (fun x ->
                        not <| (String.IsNullOrEmpty(x) || x.StartsWith("{")))
            |] |> fun arr -> String.Join("_", arr)
        else op.OperationId.Substring(skipLength)
        |> nicePascalName

    member __.GetProvidedClients() =
        let defaultHost =
            let protocol =
                match schema.Schemes with
                | [||]  -> "http" // Should use the scheme used to access the Swagger definition itself.
                | array -> array.[0]
            sprintf "%s://%s" protocol schema.Host
        let baseTy = Some typeof<ProvidedSwaggerBaseType>
        let baseCtor = baseTy.Value.GetConstructors().[0]

        List.ofArray schema.Paths
        |> List.groupBy (fun x ->
            if ignoreControllerPrefix then String.Empty //
            else
                let ind = x.OperationId.IndexOf("_")
                if ind <= 0 then String.Empty
                else x.OperationId.Substring(0, ind) )
        |> List.map (fun (clientName, operations) ->
            let typeName = nicePascalName clientName + "Client"
            let ty = ProvidedTypeDefinition(typeName, baseTy, isErased = false, hideObjectMethods = true)
            ty.AddXmlDoc (sprintf "Client for '%s_*' operations" clientName)

            ty.AddMember <|
                ProvidedConstructor(
                    [ProvidedParameter("host", typeof<string>, optionalValue = defaultHost)],
                    invokeCode = (fun args ->
                        match args with
                        | [] -> failwith "Generated constructors should always pass the instance as the first argument!"
                        | _ -> <@@ () @@>),
                    BaseConstructorCall = fun args -> (baseCtor, args))

            let methodNameScope = UniqueNameGenerator()
            operations |> List.map (fun op ->
                let skipLength = if String.IsNullOrEmpty clientName then 0 else clientName.Length + 1
                let name = OperationCompiler.GetMethodNameCandidate op skipLength ignoreOperationId
                compileOperation (methodNameScope.MakeUnique name) op)
            |> ty.AddMembers

            ty
        )