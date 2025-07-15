namespace rec Fun.Blazor

open System
open System.IO
open Microsoft.JSInterop
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Rendering
open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines
open Markdig.Extensions.TaskLists
open Markdig.Renderers
open Markdig.Renderers.Html
open MudBlazor
open Fun.Blazor
open Fun.Blazor.Operators


[<AbstractClass>]
type BlazorObjectRenderer<'TObject when 'TObject :> MarkdownObject>() =
    inherit MarkdownObjectRenderer<BlazorMarkdownRenderer, 'TObject>()

type BlazorHtmlObjectRenderer<'T when 'T :> MarkdownObject>(htmlObjectRenderer: HtmlObjectRenderer<'T>) =
    inherit BlazorObjectRenderer<'T>()

    override _.Write(renderer: BlazorMarkdownRenderer, obj: 'T) =
        let htmlRenderer = renderer.HtmlRenderer
        htmlObjectRenderer.Write(htmlRenderer, obj :> MarkdownObject)
        htmlRenderer.Writer.Flush()

        renderer.Render(
            fragment {
                match htmlRenderer.Writer.ToString() with
                | null -> ()
                | htmlContent ->
                    html.raw htmlContent
                    if htmlContent.Contains("class=\"math\"") then script { "MathJax.typeset()" }
            }
        )


type BlazorMarkdownRenderer() as this =
    inherit RendererBase()

    let mutable builder: RenderTreeBuilder | null = null
    let mutable builderContext: IComponent | null = null
    let mutable sequence = 0

    let htmlRenderer = HtmlRenderer(new StringWriter())


    do this.SetupRenders()


    override _.Render(markdownObject: MarkdownObject) = base.Write(markdownObject)


    member _.Setup(builder', context') =
        builder <- builder'
        builderContext <- context'
        sequence <- 0

    member _.SetupRenders() =
        base.ObjectRenderers.Add(CodeBlockRenderer())
        base.ObjectRenderers.Add(CodeInlineRenderer())
        base.ObjectRenderers.Add(TaskListRenderer())
        //Need to fix duplicate render
        base.ObjectRenderers.Add(ListRenderer())

        MarkdownView.MarkdownPipeline.Setup(this)
        MarkdownView.MarkdownPipeline.Setup(htmlRenderer)

        // Add wrapper for some unhandled objects to use string writer
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.HeadingRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.HtmlBlockRenderer()))
        //this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.ListRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.ParagraphRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.QuoteBlockRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.ThematicBreakRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.Inlines.AutolinkInlineRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.Inlines.DelimiterInlineRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.Inlines.EmphasisInlineRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.Inlines.HtmlEntityInlineRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.Inlines.HtmlInlineRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.Inlines.LineBreakInlineRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.Inlines.LinkInlineRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Renderers.Html.Inlines.LiteralInlineRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Extensions.Mathematics.HtmlMathBlockRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Extensions.Mathematics.HtmlMathInlineRenderer()))
        this.ObjectRenderers.Add(BlazorHtmlObjectRenderer(Markdig.Extensions.Tables.HtmlTableRenderer()))

    // Get a HtmlRenderer with empty writer
    member _.HtmlRenderer: HtmlRenderer =
        htmlRenderer.Writer <- new StringWriter()
        htmlRenderer

    member _.Render(node: NodeRenderFragment) =
        match builder with
        | null -> ()
        | builder -> sequence <- node.Invoke(builderContext, builder, sequence)


    static member MakeAttrs(content: IMarkdownObject) : AttrRenderFragment =
        AttrRenderFragment(fun _ builder sequence ->
            match content.TryGetAttributes() with
            | null -> sequence
            | attrs ->
                let mutable sequence = sequence
                match attrs.Classes with
                | null -> ()
                | classes ->
                    builder.AddAttribute(sequence, "class", String.concat " " classes)
                    sequence <- sequence + 1
                match attrs.Properties with
                | null -> ()
                | properties ->
                    for KeyValue(k, v) in properties do
                        builder.AddAttribute(sequence, k, v)
                        sequence <- sequence + 1
                sequence
        )

    static member MakeNode(content: LeafBlock) : NodeRenderFragment =
        NodeRenderFragment(fun _ builder sequence ->
            let content = BlazorMarkdownRenderer.MakeString(content)
            builder.AddContent(sequence, content)
            sequence + 1
        )

    static member MakeString(content: LeafBlock) : string =
        if box content.Lines.Lines <> null then
            let sb = Fun.Blazor.Utils.Internal.stringBuilderPool.Get()
            try
                for slice in content.Lines.Lines do
                    if slice.Slice.Text <> null then sb.Append(slice.Slice).AppendLine() |> ignore
                sb.ToString()
            finally
                Fun.Blazor.Utils.Internal.stringBuilderPool.Return(sb)
        else
            ""


type MarkdownView =

    static let markdownPipeline = lazy (MarkdownPipelineBuilder().UseAdvancedExtensions().Build())

    static let blazorMarkdownRendererPool = lazy (Fun.Blazor.Utils.Internal.objectPoolProvider.Create<BlazorMarkdownRenderer>())

    static member MarkdownPipeline: MarkdownPipeline = markdownPipeline.Value

    static member Create(md: string) = div {
        class' "markdown-body"
        NodeRenderFragment(fun comp builder index ->
            builder.OpenRegion(index)

            let renderer = blazorMarkdownRendererPool.Value.Get()
            try
                renderer.Setup(builder, comp)
                let document = Markdig.Markdown.Parse(md, markdownPipeline.Value)
                renderer.Render(document) |> ignore
            finally
                blazorMarkdownRendererPool.Value.Return(renderer)

            builder.CloseRegion()
            index + 1
        )
    }


type CodeBlockRenderer() =
    inherit BlazorObjectRenderer<CodeBlock>()

    static let mermaidCountLock = obj ()
    static let mutable mermaidCount = 0

    // https://github.com/mermaid-js/mermaid/blob/develop/packages/mermaid/src/utils.ts#L755
    static let getNextMermaidIdSeed () =
        lock
            mermaidCountLock
            (fun () ->
                mermaidCount <- mermaidCount + 1
                String.Concat [|
                    for _ in 1..mermaidCount do
                        "0"
                |]
            )

    override _.Write(renderer: BlazorMarkdownRenderer, obj: CodeBlock) =
        let contentLength =
            match box obj.Lines.Lines with
            | null -> 0
            | _ -> obj.Lines.Lines |> Seq.sumBy _.Slice.Length
        let codeId = $"code-{obj.Line}-{contentLength}"
        renderer.Render(
            html.inject (
                codeId,
                fun (shareStore: IShareStore, dialogService: IDialogService, JS: IJSRuntime) ->
                    let copyBtn = MudIconButton'' {
                        key "copy"
                        Size Size.Small
                        Variant Variant.Filled
                        Icon Icons.Material.Outlined.ContentCopy
                        OnClick(fun _ -> task { do! JS.CopyInnerText(codeId) })
                    }
                    let previewBtn (showDialog: CodeBlock -> unit) = MudIconButton'' {
                        key "preview"
                        Size Size.Small
                        Variant Variant.Filled
                        Icon Icons.Material.Outlined.RemoveRedEye
                        OnClick(fun _ -> showDialog obj)
                    }
                    let mainContent (codeId: string) = region {
                        match obj with
                        | :? FencedCodeBlock as fc when "mermaid".Equals(fc.Info, StringComparison.OrdinalIgnoreCase) ->
                            pre {
                                id codeId
                                BlazorMarkdownRenderer.MakeAttrs(obj)
                                class' "mermaid"
                                BlazorMarkdownRenderer.MakeNode(obj)
                            }
                            adaptiview (key = struct (obj, "script")) {
                                let! isDarkMode = shareStore.IsDarkMode
                                script {
                                    $$"""
                                    mermaid.init(
                                        {
                                            securityLevel: 'loose',
                                            theme: '{{if isDarkMode then "dark" else "neutral"}}',
                                            deterministicIds: true, 
                                            deterministicIDSeed: '{{getNextMermaidIdSeed ()}}',
                                        },
                                        document.getElementById('{{codeId}}')
                                    );
                                    """
                                }
                            }
                        | _ ->
                            pre {
                                code {
                                    id codeId
                                    BlazorMarkdownRenderer.MakeAttrs(obj)
                                    BlazorMarkdownRenderer.MakeNode(obj)
                                }
                            }
                            script {
                                key (struct (codeId, "script"))
                                $"Prism.highlightElement(document.getElementById('{codeId}'))"
                            }
                    }
                    div {
                        style {
                            positionRelative
                            overflowHidden
                        }
                        mainContent codeId
                        div {
                            style {
                                positionAbsolute
                                top 12
                                right 12
                                displayFlex
                                alignItemsCenter
                                gap 8
                                zIndex 1
                            }
                            previewBtn (fun obj ->
                                match obj with
                                | :? FencedCodeBlock as fc when "html".Equals(fc.Info, StringComparison.OrdinalIgnoreCase) ->
                                    dialogService.PreviewHtml(BlazorMarkdownRenderer.MakeString(obj))
                                | _ ->
                                    dialogService.Show(
                                        DialogOptions(MaxWidth = MaxWidth.ExtraLarge, FullWidth = true),
                                        fun ctx -> MudDialog'' {
                                            Header "Preview" ctx.Close
                                            DialogContent(
                                                div {
                                                    style {
                                                        overflowHidden
                                                        height "calc(100vh - 200px)"
                                                    }
                                                    styleElt {
                                                        ruleset """pre[class*="language-"]""" {
                                                            overflowYAuto
                                                            height "100%"
                                                            maxHeight "100% !important"
                                                        }
                                                    }
                                                    mainContent (codeId + "-preview")
                                                }
                                            )
                                        }
                                    )
                            )
                            copyBtn
                        }
                    }
            )
        )

type CodeInlineRenderer() =
    inherit BlazorObjectRenderer<CodeInline>()

    override _.Write(renderer: BlazorMarkdownRenderer, obj: CodeInline) =
        let codeId = $"code-{obj.Line}-{obj.ContentSpan.Length}"
        renderer.Render(
            span {
                style { positionRelative }
                code {
                    id codeId
                    BlazorMarkdownRenderer.MakeAttrs(obj)
                    NodeRenderFragment(fun _ builder sequence ->
                        builder.AddContent(sequence, obj.ContentSpan.ToString())
                        sequence + 1
                    )
                }
                script {
                    key (struct (codeId, "script"))
                    "window.highlightCode()"
                }
            }
        )


type TaskListRenderer() =
    inherit BlazorObjectRenderer<TaskList>()

    override _.Write(renderer: BlazorMarkdownRenderer, obj: TaskList) =
        renderer.Render(
            input {
                BlazorMarkdownRenderer.MakeAttrs(obj)
                type' InputTypes.checkbox
                disabled true
                checked' obj.Checked
            }
        )

type ListRenderer() =
    inherit BlazorObjectRenderer<ListBlock>()

    override _.Write(renderer: BlazorMarkdownRenderer, obj: ListBlock) =
        renderer.Render(
            region {
                let items = fragment {
                    for item in obj do
                        li {
                            BlazorMarkdownRenderer.MakeAttrs(item)
                            renderer.WriteChildren(item :?> ListItemBlock)
                        }
                }
                if obj.IsOrdered then
                    ol {
                        domAttr { if obj.BulletType <> '1' then type' (obj.BulletType.ToString()) }
                        domAttr {
                            match obj.OrderedStart with
                            | null
                            | "1" -> ()
                            | x -> "start" => x
                        }
                        BlazorMarkdownRenderer.MakeAttrs(obj)
                        items
                    }
                else
                    ul {
                        BlazorMarkdownRenderer.MakeAttrs(obj)
                        items
                    }
            }
        )
