namespace Brainloop.Entry

open Fun.Blazor
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Web


type Index() as this =
    inherit FunComponent()

    member _.MapAsset(x) = base.Assets[x]

    override _.Render() =
        let iconUrl = base.Assets["favicon.png"]
        fragment {
            doctype "html"
            html' {
                lang "EN"
                head {
                    baseUrl "/"
                    meta { charset "utf-8" }
                    meta {
                        name "viewport"
                        content "width=device-width, initial-scale=1.0"
                    }
                    link {
                        rel "icon"
                        type' "image/png"
                        href iconUrl
                    }
                    styleElt {
                        ruleset ".active" {
                            color "green"
                            fontWeightBold
                        }
                    }
                    stylesheet (this.MapAsset("_content/MudBlazor/MudBlazor.min.css"))
                    stylesheet (this.MapAsset("css/google-font.css"))
                    stylesheet (this.MapAsset("css/github-markdown.css"))
                    stylesheet (this.MapAsset("excalidraw/index-Cc5PzV2C.css"))
                    styleElt {
                        html.raw
                            """
                            body {
                                background-color: white;
                                color: black;
                            }
                            @media (prefers-color-scheme: dark) {
                                body {
                                    background-color: #0a0b0e;
                                    color: white;
                                }
                            }
                            """
                    }
                    html.blazor<ImportMap> ()
                    HeadOutlet'' { renderMode (InteractiveServerRenderMode(prerender = false)) }
                }
                body {
                    html.blazor<Routes> (InteractiveServerRenderMode(prerender = false))
                    script { src (this.MapAsset("_content/MudBlazor/MudBlazor.min.js")) }
                    script { src (this.MapAsset("https://cdnjs.cloudflare.com/ajax/libs/prism/1.23.0/components/prism-core.min.js")) }
                    script { src (this.MapAsset("https://cdnjs.cloudflare.com/ajax/libs/prism/1.23.0/plugins/autoloader/prism-autoloader.min.js")) }
                    script { src (this.MapAsset("js/tex-mml-chtml@3.2.2.js")) }
                    script { src (this.MapAsset("js/mermaid.min@11.js")) }
                    //script { src (this.MapAsset("js/html2canvas.min@1.4.1.js")) }
                    script { src (this.MapAsset("js/html2Image@1.11.13.js")) }
                    script { src (this.MapAsset("js/medium-zoom@1.1.0.min.js")) }
                    script {
                        src (this.MapAsset("excalidraw/index-Cs2HrhSQ.js"))
                        type' "module"
                        async true
                    }
                    script { src (this.MapAsset("_content/BlazorMonaco/jsInterop.js")) }
                    script { src (this.MapAsset("_content/BlazorMonaco/lib/monaco-editor/min/vs/loader.js")) }
                    script { src (this.MapAsset("_content/BlazorMonaco/lib/monaco-editor/min/vs/editor/editor.main.js")) }
                    interopScript
                    script { src (this.MapAsset("_framework/blazor.server.js")) }
                }
            }
        }
