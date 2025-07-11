[<AutoOpen>]
module Fun.Blazor.Form

open System
open Microsoft.AspNetCore.Components
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
    member inline this.Value'
        ([<InlineIfLambda>] render: Fun.Blazor.AttrRenderFragment, (binding, errors): ('T * ('T -> unit)) * string list)
        =
        let render = this.Value'(render, binding)
        this.Errors(render, errors)


type KeyValueField =

    static member Create
        (kvs: Map<string, string>, setKvs: Map<string, string> -> unit, ?createNewPlacehold, ?disableCreateNew, ?readOnlyKey)
        =
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
                        }
                    }
                    MudItem'' {
                        xs 8
                        MudTextField'' {
                            Label "Value"
                            AutoFocus
                            Value v
                            ValueChanged(fun v -> kvs |> Map.add k v |> setKvs)
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
