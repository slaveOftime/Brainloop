[<AutoOpen>]
module Fun.Blazor.Form

open System
open FSharp.Data.Adaptive
open Microsoft.AspNetCore.Components
open BlazorMonaco
open BlazorMonaco.Editor
open MudBlazor.DslInternals
open MudBlazor
open Fun.Result


type MudFormComponentBuilder<'FunBlazorGeneric, 'T, 'U when 'FunBlazorGeneric :> IComponent> with

    [<CustomOperation("Errors")>]
    member inline this.Errors([<InlineIfLambda>] render: Fun.Blazor.AttrRenderFragment, errors: string seq) =
        let render = this.Error(render, Seq.length errors > 0)
        this.ErrorText(render, String.concat ", " errors)

type MudBaseInputBuilder<'FunBlazorGeneric, 'T when 'FunBlazorGeneric :> Microsoft.AspNetCore.Components.IComponent> with

    [<CustomOperation("Value'")>]
    member inline this.Value'([<InlineIfLambda>] render: Fun.Blazor.AttrRenderFragment, (binding, errors): ('T * ('T -> unit)) * string list) =
        let render = this.Value'(render, binding)
        this.Errors(render, errors)


type KeyValueField =

    static member Create
        (
            kvs: Map<string, string>,
            setKvs: Map<string, string> -> unit,
            ?createNewPlacehold,
            ?disableCreateNew,
            ?readOnlyKey,
            ?isSensitiveKey: string -> bool,
            ?setAsSensitive: string -> bool -> unit
        ) =
        MudGrid'' {
            Spacing 2
            region {
                for KeyValue(k, v) in kvs do
                    MudItem'' {
                        xs 4
                        MudTextField'' {
                            Label "Key"
                            AutoFocus false
                            ReadOnly(defaultArg readOnlyKey false)
                            Value k
                            ValueChanged(fun v ->
                                match v with
                                | SafeString _ -> kvs |> Map.add v (kvs |> Map.tryFind k |> Option.defaultValue "") |> Map.remove k
                                | _ -> kvs |> Map.remove k
                                |> setKvs
                            )
                            Adornment(
                                match k, setAsSensitive, isSensitiveKey with
                                | SafeString _, Some _, Some _ -> Adornment.End
                                | _ -> Adornment.None
                            )
                            AdornmentIcon Icons.Material.Filled.RemoveRedEye
                            OnAdornmentClick(fun _ ->
                                match k, setAsSensitive with
                                | SafeString _, Some fn -> isSensitiveKey |> Option.map (fun fn -> fn k |> not) |> Option.defaultValue true |> fn k
                                | _ -> ()
                            )
                        }
                    }
                    MudItem'' {
                        xs 7
                        let textFieldAttrs = MudTextField'' {
                            Label "Value"
                            AutoFocus
                            Value v
                            ValueChanged(fun v -> kvs |> Map.add k v |> setKvs)
                            asAttrRenderFragment
                        }
                        match isSensitiveKey with
                        | Some isSensitive when isSensitive k -> adapt {
                            let! showPassword, setShowPassword = cval(false).WithSetter()
                            MudTextField''<string> {
                                textFieldAttrs
                                InputType(if showPassword then InputType.Text else InputType.Password)
                                Adornment Adornment.End
                                AdornmentIcon(
                                    if showPassword then
                                        Icons.Material.Outlined.Visibility
                                    else
                                        Icons.Material.Outlined.VisibilityOff
                                )
                                OnAdornmentClick(fun _ -> setShowPassword (not showPassword))
                            }
                          }
                        | _ -> MudTextField''<string> { textFieldAttrs }
                    }
                    MudItem'' {
                        xs 1
                        MudIconButton'' {
                            Icon Icons.Material.Filled.Close
                            Disabled(defaultArg readOnlyKey false)
                            OnClick(fun _ -> kvs |> Map.remove k |> setKvs)
                            style {
                                marginTop 8
                                positionAbsolute
                            }
                        }
                    }
            }
            if defaultArg disableCreateNew false |> not then
                MudItem'' {
                    key (Random.Shared.Next())
                    xs 4
                    MudTextField'' {
                        Label(defaultArg createNewPlacehold "Add new key and press enter")
                        AutoFocus false
                        Value ""
                        ValueChanged(fun k -> kvs |> Map.add k "" |> setKvs)
                    }
                }
        }


type MonacoField =

    static member Create
        (
            label: string,
            textContent: string,
            ?setTextContent: string -> unit,
            ?language: string,
            ?classIdentifier: string,
            ?monacoStyle: Fun.Css.Internal.CombineKeyValue
        ) =
        let classIdentifier = defaultArg classIdentifier $"monaco-editor-field-{Random.Shared.Next()}"
        MudField'' {
            Label label
            Variant Variant.Outlined
            div {
                class' classIdentifier
                html.inject (
                    textContent,
                    fun (shareStore: IShareStore) ->
                        let mutable hasChanges = false
                        let mutable inputRef: StandaloneCodeEditor | null = null
                        adapt {
                            let! isDarkMode = shareStore.IsDarkMode
                            StandaloneCodeEditor'' {
                                key textContent
                                ConstructionOptions(fun _ ->
                                    StandaloneEditorConstructionOptions(
                                        Value = textContent,
                                        FontSize = 16,
                                        Language = defaultArg language "markdown",
                                        AutomaticLayout = true,
                                        ReadOnly = setTextContent.IsNone,
                                        GlyphMargin = false,
                                        Folding = false,
                                        LineDecorationsWidth = 0,
                                        LineNumbers = "off",
                                        WordWrap = "on",
                                        Minimap = EditorMinimapOptions(Enabled = false),
                                        AcceptSuggestionOnEnter = "on",
                                        FixedOverflowWidgets = true,
                                        Theme = if isDarkMode then "vs-dark" else "vs-light"
                                    )
                                )
                                OnDidChangeModelContent(fun _ -> hasChanges <- true)
                                OnDidBlurEditorWidget(fun _ -> task {
                                    if hasChanges then
                                        match inputRef with
                                        | null -> ()
                                        | ref ->
                                            let! text = ref.GetValue()
                                            setTextContent |> Option.iter (fun fn -> fn text)
                                })
                                ref (fun x -> inputRef <- x)
                            }
                        }
                )
                styleElt {
                    ruleset $".{classIdentifier} .monaco-editor-container" {
                        height "300px"
                        defaultArg monacoStyle html.emptyCss
                    }
                }
            }
        }
