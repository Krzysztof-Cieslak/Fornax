[<AutoOpen>]
module Generator

open System
open System.IO
open System.Diagnostics

module internal Utils =
    let rec retry times fn =
        if times > 1 then
            try
                fn()
            with
            | _ ->
                System.Threading.Thread.Sleep 50
                retry (times - 1) fn
        else
            fn()

    let memoizeParser f =
        let cache = ref Map.empty
        fun (x : string) (y : System.Type) ->
            let input = (x,y.GetHashCode())
            match (!cache).TryFind(input) with
            | Some res -> res
            | None ->
                let res = f x y
                cache := (!cache).Add(input,res)
                res

    let memoize f =
        let cache = ref Map.empty
        fun x ->
            match (!cache).TryFind(x) with
            | Some res -> res
            | None ->
                let res = f x
                cache := (!cache).Add(x,res)
                res

    let memoizeScriptFile (f : string -> Result<'a, string>) =
        let resultCache = ref Map.empty
        let contentCache = ref Map.empty
        fun (x : string) ->
            let rec getContent (f : string) =
                let dir = Path.GetDirectoryName f
                let content = retry 2 (fun _ -> File.ReadAllLines f)
                let contetnMap' = [(f, content)]
                let loads = content |> Array.where (fun n -> n.Contains "#load")
                let relativeFiles = loads |> Array.map (fun n -> (n.Split '"').[1])
                if relativeFiles.Length > 0 then
                    relativeFiles
                    |> Array.fold (fun acc e ->
                        let pth = Path.Combine(dir, e)
                        [yield! acc; yield! getContent pth ]) contetnMap'
                else contetnMap'

            try
                let ctn = getContent x

                match (!resultCache).TryFind(x) with
                | Some res ->
                    match (!contentCache).TryFind(x) with
                    | Some r when r = ctn -> res
                    | _ ->
                        let res = f x
                        resultCache := (!resultCache).Add(x,res)
                        contentCache := (!contentCache).Add(x,ctn)
                        res
                | None ->
                    let res = f x
                    resultCache := (!resultCache).Add(x,res)
                    contentCache := (!contentCache).Add(x,ctn)
                    res
            with
            | :? FileNotFoundException as fnf ->
                Error fnf.Message
            | _ -> reraise ()

module Evaluator =
    open System.Globalization
    open System.Text
    open Microsoft.FSharp.Compiler.Interactive.Shell
    open FSharp.Quotations.Evaluator
    open FSharp.Reflection

    let private sbOut = StringBuilder()
    let private sbErr = StringBuilder()
    let internal fsi () =
        let inStream = new StringReader("")
        let outStream = new StringWriter(sbOut)
        let errStream = new StringWriter(sbErr)
        try
            let fsiConfig = FsiEvaluationSession.GetDefaultConfiguration()
            let argv = [| "/temp/fsi.exe"; "--define:FORNAX"|]
            FsiEvaluationSession.Create(fsiConfig, argv, inStream, outStream, errStream)
        with
        | ex ->
            printfn "Error: %A" ex
            printfn "Inner: %A" ex.InnerException
            printfn "ErrorStream: %s" (errStream.ToString())
            raise ex

    let private getOpen (path : string) =
        let filename = Path.GetFileNameWithoutExtension path
        let textInfo = (CultureInfo("en-US", false)).TextInfo
        textInfo.ToTitleCase filename

    let private getLoad (path : string) =
        path.Replace("\\", "\\\\")

    let private invokeFunction (f : obj) (args : obj seq) =
        let rec helper (next : obj) (args : obj list)  =
            match args.IsEmpty with
            | false ->
                let fType = next.GetType()
                if FSharpType.IsFunction fType then
                    let methodInfo =
                        fType.GetMethods()
                        |> Array.filter (fun x -> x.Name = "Invoke" && x.GetParameters().Length = 1)
                        |> Array.head
                    let res = methodInfo.Invoke(next, [| args.Head |])
                    helper res args.Tail
                else None
            | true ->
                Some next
        helper f (args |> List.ofSeq )

    let private createInstance (input : FsiValue) (args : Map<string, obj>) =
        let mType = input.ReflectionValue :?> Type
        let fields =
            mType.GetMembers()
            |> Array.skipWhile (fun n -> n.Name <> ".ctor")
            |> Array.skip 1
            |> Array.map (fun n -> args.[n.Name])

        let ctor = mType.GetConstructors().[0]
        ctor.Invoke(fields)

    let private compileExpression (input : FsiValue) =
        let genExpr = input.ReflectionValue :?> Quotations.Expr
        QuotationEvaluator.CompileUntyped genExpr

    let private getContentFromLayout' (fsi : FsiEvaluationSession) (layoutPath : string) =
        let filename = getOpen layoutPath
        let load = getLoad layoutPath

        let tryFormatErrorMessage message (errors : 'a []) =
            if errors.Length > 0 then
                sprintf "%s: %A" message errors |> Some
            else
                None

        let _, loadErrors = fsi.EvalInteractionNonThrowing(sprintf "#load \"%s\";;" load)
        let loadErrorMessage =
            tryFormatErrorMessage "Load Errors" loadErrors

        let _, openErrors = fsi.EvalInteractionNonThrowing(sprintf "open %s;;" filename)
        let openErrorMessage =
            tryFormatErrorMessage "Open Errors" openErrors

        let modelType, modelErrors = fsi.EvalExpressionNonThrowing "typeof<Model>"
        let modelErrorMesage =
            tryFormatErrorMessage "Get model Errors" modelErrors

        let siteModelType, siteModelErrors = fsi.EvalExpressionNonThrowing "typeof<SiteModel.SiteModel>"
        let siteModelErrorMessage =
            tryFormatErrorMessage "Get site model Errors" siteModelErrors

        let funType, layoutErrors = fsi.EvalExpressionNonThrowing "<@@ fun a b c d -> (generate a b (Post.Construct c) d) |> HtmlElement.ToString @@>"
        let layoutErrorMessage =
            tryFormatErrorMessage "Get layout Errors" layoutErrors

        let completeErrorReport =
            [ loadErrorMessage
              openErrorMessage
              modelErrorMesage
              siteModelErrorMessage
              layoutErrorMessage ]
            |> List.filter (Option.isSome)
            |> List.map (fun v -> v.Value)
            |> List.fold (fun state message -> state + Environment.NewLine + message) ""
            |> (fun s -> s.Trim(Environment.NewLine.ToCharArray()))

        match modelType, siteModelType, funType with
        | Choice1Of2 (Some mt), Choice1Of2 (Some smt), Choice1Of2 (Some ft) ->
            Ok (mt, smt, ft)
        | _ -> Error completeErrorReport

    // let private getContentFromLayout = Utils.memoizeScriptFile getContentFromLayout'
    let private getContentFromLayout = getContentFromLayout'

    ///`layoutPath` - absolute path to `.fsx` file containing the layout
    ///`getSiteModel` - function generating instance of site settings model of given type
    ///`getContentModel` - function generating instance of page mode of given type
    ///`body` - content of the post (in html)
    let evaluate (fsi : FsiEvaluationSession) posts (layoutPath : string) (getSiteModel : System.Type -> obj) (getContentModel : System.Type -> obj * string) =
        getContentFromLayout fsi layoutPath
        |> Result.bind (fun (mt, smt, ft) ->
            let modelInput, body = getContentModel (mt.ReflectionValue :?> Type)
            let siteInput = getSiteModel (smt.ReflectionValue :?> Type)
            let generator = compileExpression ft

            invokeFunction generator [siteInput; modelInput; box posts; box body]
            |> Option.bind (tryUnbox<string>)
            |> function
                | Some s -> Ok s
                | None -> sprintf "The expression for %s couldn't be compiled" layoutPath |> Error)

// Module to print colored message in the console
module Logger =
    let consoleColor (fc : ConsoleColor) =
        let current = Console.ForegroundColor
        Console.ForegroundColor <- fc
        { new IDisposable with
              member x.Dispose() = Console.ForegroundColor <- current }

    let informationfn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Green in printfn "%s" s) str
    let error str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Red in printf "%s" s) str
    let errorfn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Red in printfn "%s" s) str

module ContentParser =
    open Configuration
    open Markdig

    let private isSeparator (input : string) =
        input.StartsWith "---"

    let private isLayout (input : string) =
        input.StartsWith "layout:"

    let markdownPipeline =
        MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseGridTables()
            .Build()

    ///`fileContent` - content of page to parse. Usually whole content of `.md` file
    ///`modelType` - `System.Type` representing type used as model of the page
    /// returns tupple of:
    /// - instance of model record
    /// - transformed to HTML page content
    let parse (fileContent : string) (modelType : Type) =
        let fileContent = fileContent.Split '\n'
        let fileContent = fileContent |> Array.skip 1 //First line must be ---
        let indexOfSeperator = fileContent |> Array.findIndex isSeparator
        let config, content = fileContent |> Array.splitAt indexOfSeperator

        let content = content |> Array.skip 1 |> String.concat "\n"
        let config = config |> String.concat "\n"
        let contentOutput = Markdown.ToHtml(content, markdownPipeline)
        let configOutput = Yaml.parse modelType config
        configOutput, contentOutput

    ///`fileContent` - content of page to parse. Usually whole content of `.md` file
    ///returns name of layout that should be used for the page
    let getLayout (fileContent : string) =
        fileContent.Split '\n'
        |> Array.find isLayout
        |> fun n -> n.Replace("layout:", "").Trim()

    ///`fileContent` - content of page to parse. Usually whole content of `.md` file
    ///returns content of config that should be used for the page
    let getConfig (fileContent : string) =
        let fileContent = fileContent.Split '\n'
        let fileContent = fileContent |> Array.skip 1 //First line must be ---
        let indexOfSeperator = fileContent |> Array.findIndex isSeparator
        fileContent
        |> Array.splitAt indexOfSeperator
        |> fst
        |> String.concat "\n"

    ///`fileContent` - content of page to parse. Usually whole content of `.md` file
    ///returns HTML version of content of the page
    let getContent (fileContent : string) =
        let fileContent = fileContent.Split '\n'
        let fileContent = fileContent |> Array.skip 1 //First line must be ---
        let indexOfSeperator = fileContent |> Array.findIndex isSeparator
        let _, content = fileContent |> Array.splitAt indexOfSeperator

        let content = content |> Array.skip 1 |> String.concat "\n"
        Markdown.ToHtml(content, markdownPipeline)

    let containsLayout (fileContent : string) =
        fileContent.Split '\n'
        |> Array.exists isLayout

    let compileMarkdown (fileContent : string) =
        Markdown.ToHtml(fileContent, markdownPipeline)

type Link = string
type Title = string
type Author = string option

/// Optional published date.
type Published = DateTime option

/// Tags associated with the post.
type Tags = string list

/// Represents the converted HTML of the .md content.
type Content = string

module SiteSettingsParser =
    open Configuration

    ///`fileContent` - site settings to parse. Usually whole content of `site.yml` file
    ///`modelType` - `System.Type` representing type used as model of the global site settings
    let parse fileContent (modelType : Type) =
        Yaml.parse modelType fileContent

module StyleParser =

    //`fileContent` - content of `.less` file to parse
    let parseLess fileContent =
        dotless.Core.Less.Parse fileContent

let private contentParser : string -> System.Type -> obj * string  = Utils.memoizeParser ContentParser.parse
let private settingsParser : string -> System.Type -> obj = Utils.memoizeParser SiteSettingsParser.parse
let private getLayout : string -> string = Utils.memoize  ContentParser.getLayout
let private getConfig : string -> string = Utils.memoize  ContentParser.getConfig
let private getContent : string -> string = Utils.memoize  ContentParser.getContent

let private containsLayout : string -> bool = Utils.memoize ContentParser.containsLayout
let private compileMarkdown : string -> string = Utils.memoize ContentParser.compileMarkdown
let private parseLess : string -> string = Utils.memoize StyleParser.parseLess

let private trimString (str : string) =
    str.Trim().TrimEnd('"').TrimStart('"')

let getPosts (projectRoot : string) =
    let postsPath = Path.Combine(projectRoot, "posts")
    Directory.GetFiles postsPath
    |> Array.filter (fun n -> n.EndsWith ".md")
    |> Array.map (fun n ->
        // All the text in the .md file.
        let text = Utils.retry 2 (fun _ -> File.ReadAllText n)

        let config = getConfig text |> String.split '\n'

        let content = getContent text

        let link = "/" + Path.Combine("posts", (n |> Path.GetFileNameWithoutExtension) + ".html").Replace("\\", "/")

        let title = config |> List.find (fun n -> n.ToLower().StartsWith "title" ) |> fun n -> n.Split(':').[1] |> trimString

        let author =
            try
                config |> List.tryFind (fun n -> n.ToLower().StartsWith "author" ) |> Option.map (fun n -> n.Split(':').[1] |> trimString)
            with
            | _ -> None

        let published =
            try
                config |> List.tryFind (fun n -> n.ToLower().StartsWith "published" ) |> Option.map (fun n -> n.Split(':').[1] |> trimString |> DateTime.Parse)
            with
            | _ -> None

        let tags =
            try
                let x =
                    config
                    |> List.tryFind (fun n -> n.ToLower().StartsWith "tags" )
                    |> Option.map (fun n -> n.Split(':').[1] |> trimString |> fun n -> n.Split ',' |> Array.toList )
                defaultArg x []
            with
            | _ -> []

        ((link:Link), (title:Title), (author:Author), (published:Published), (tags:Tags), (content:Content)))

let injectWebsocketCode (webpage:string) = 
    let websocketScript =
        """
        <script type="text/javascript">
          var wsUri = "ws://localhost:8080/websocket";
      function init()
      {
        websocket = new WebSocket(wsUri);
        websocket.onclose = function(evt) { onClose(evt) };
      }
      function onClose(evt)
      {
        console.log('closing');
        websocket.close();
        document.location.reload();
      }
      window.addEventListener("load", init, false);
      </script>
        """
    let head = "<head>"
    let index = webpage.IndexOf head
    webpage.Insert ( (index + head.Length),websocketScript)
exception FornaxGeneratorException of string

type GeneratorMessage = string

type GeneratorResult =
    | GeneratorIgnored
    | GeneratorSuccess of GeneratorMessage option
    | GeneratorFailure of GeneratorMessage

///`projectRoot` - path to the root of website
///`page` - path to page that should be generated
let generate (disableLiveRefresh:bool) posts (projectRoot : string) (page : string) =
    let startTime = DateTime.Now
    let contentPath = Path.Combine(projectRoot, page)
    let settingsPath = Path.Combine(projectRoot, "_config.yml")
    let outputPath =
        let p = Path.ChangeExtension(page, ".html")
        Path.Combine(projectRoot, "_public", p)

    let contentText = Utils.retry 2 (fun _ -> File.ReadAllText contentPath)

    if containsLayout contentText then
        let settingsText = Utils.retry 2 (fun _ -> File.ReadAllText settingsPath)
        let layout = getLayout contentText

        let settingsLoader = settingsParser settingsText
        let modelLoader = contentParser contentText
        let layoutPath = Path.Combine(projectRoot, "layouts", layout + ".fsx")

        use fsiSession = Evaluator.fsi ()
        let result =
            Evaluator.evaluate fsiSession posts layoutPath settingsLoader modelLoader
            |> Result.map(fun strContent ->
                if not disableLiveRefresh then
                    injectWebsocketCode strContent
                else strContent
            )

        match result with
        | Ok r ->
            let dir = Path.GetDirectoryName outputPath
            if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
            File.WriteAllText(outputPath, r)
            let endTime = DateTime.Now
            let ms = (endTime - startTime).Milliseconds
            sprintf "[%s] '%s' generated in %dms" (endTime.ToString("HH:mm:ss")) outputPath ms
            |> Some
            |> GeneratorSuccess
        | Error message ->
            let endTime = DateTime.Now
            sprintf "[%s] '%s' generation failed" (endTime.ToString("HH:mm:ss")) outputPath
            |> (fun s -> message + Environment.NewLine + s)
            |> GeneratorFailure
    else
        let r = compileMarkdown contentText
        let dir = Path.GetDirectoryName outputPath
        if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
        File.WriteAllText(outputPath, r)
        let endTime = DateTime.Now
        let ms = (endTime - startTime).Milliseconds
        sprintf "[%s] '%s' generated in %dms" (endTime.ToString("HH:mm:ss")) outputPath ms
        |> Some
        |> GeneratorSuccess

///`projectRoot` - path to the root of website
///`path` - path to file that should be copied
let copyStaticFile  (projectRoot : string) (path : string) =
    let inputPath = Path.Combine(projectRoot, path)
    let outputPath = Path.Combine(projectRoot, "_public", path)
    let dir = Path.GetDirectoryName outputPath
    if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
    File.Copy(inputPath, outputPath, true)
    GeneratorSuccess None

///`projectRoot` - path to the root of website
///`path` - path to `.less` file that should be copied
let generateFromLess (projectRoot : string) (path : string) =
    let startTime = DateTime.Now
    let inputPath = Path.Combine(projectRoot, path)
    let path' = Path.ChangeExtension(path, ".css")
    let outputPath = Path.Combine(projectRoot, "_public", path')
    let dir = Path.GetDirectoryName outputPath
    if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
    let res = Utils.retry 2 (fun _ -> File.ReadAllText inputPath) |> parseLess
    File.WriteAllText(outputPath, res)
    let endTime = DateTime.Now
    let ms = (endTime - startTime).Milliseconds
    sprintf "[%s] '%s' generated in %dms" (endTime.ToString("HH:mm:ss")) outputPath ms
    |> Some
    |> GeneratorSuccess

///`projectRoot` - path to the root of website
///`path` - path to `.scss` or `.sass` file that should be copied
let generateFromSass (projectRoot : string) (path : string) =
    let startTime = DateTime.Now
    let inputPath = Path.Combine(projectRoot, path)
    let path' = Path.ChangeExtension(path, ".css")
    let outputPath = Path.Combine(projectRoot, "_public", path')
    let dir = Path.GetDirectoryName outputPath
    if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore

    let psi = ProcessStartInfo()
    psi.FileName <- "sass"
    psi.Arguments <- sprintf "%s %s" inputPath outputPath
    psi.CreateNoWindow <- true
    psi.WindowStyle <- ProcessWindowStyle.Hidden
    psi.UseShellExecute <- true

    try
        let proc = Process.Start psi
        proc.WaitForExit()
        let endTime = DateTime.Now
        let ms = (endTime - startTime).Milliseconds
        sprintf "[%s] '%s' generated in %dms" (endTime.ToString("HH:mm:ss")) outputPath ms
        |> Some
        |> GeneratorSuccess
    with
        | :? System.ComponentModel.Win32Exception as ex ->
            let endTime = DateTime.Now
            sprintf "[%s] Generation of '%s' failed. " (endTime.ToString("HH:mm:ss")) path'
            |> fun s -> s + Environment.NewLine + "Please check you have installed the Sass compiler if you are going to be using files with extension .scss. https://sass-lang.com/install"
            |> GeneratorFailure

let private (|Ignored|Markdown|Less|Sass|StaticFile|) (filename : string) =
    let ext = Path.GetExtension filename
    if filename.Contains "_public" || filename.Contains "_bin" || filename.Contains "_lib" || filename.Contains "_data" || filename.Contains "_settings" || filename.Contains "_config.yml" || ext = ".fsx" || filename.Contains ".sass-cache" || filename.Contains ".git" || filename.Contains ".ionide" then Ignored
    elif ext = ".md" then Markdown
    elif ext = ".less" then Less
    elif ext = ".sass" || ext =".scss" then Sass
    else StaticFile

///`projectRoot` - path to the root of website
let generateFolder (disableLiveRefresh:bool) (projectRoot : string) =
    let relative toPath fromPath =
        let toUri = Uri(toPath)
        let fromUri = Uri(fromPath)
        toUri.MakeRelativeUri(fromUri).OriginalString

    let projectRoot =
        if projectRoot.EndsWith (string Path.DirectorySeparatorChar) then projectRoot
        else projectRoot + (string Path.DirectorySeparatorChar)

    let posts = getPosts projectRoot

    let logResult (result : GeneratorResult) =
        match result with
        | GeneratorIgnored -> ()
        | GeneratorSuccess None -> ()
        | GeneratorSuccess (Some message) ->
            Logger.informationfn "%s" message
        | GeneratorFailure message ->
            // if one generator fails we want to exit early and report the problem to the operator
            raise (FornaxGeneratorException message)

    Directory.GetFiles(projectRoot, "*", SearchOption.AllDirectories)
    |> Array.iter (fun n ->
        match n with
        | Ignored -> GeneratorIgnored
        | Markdown -> n |> relative projectRoot |> generate disableLiveRefresh posts projectRoot
        | Less -> n |> relative projectRoot |> generateFromLess projectRoot
        | Sass  -> n |> relative projectRoot |> generateFromSass projectRoot
        | StaticFile -> n |> relative projectRoot |> copyStaticFile projectRoot
        |> logResult)
