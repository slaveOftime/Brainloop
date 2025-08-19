namespace Brainloop.Loop

open System
open System.Collections
open FSharp.Data.Adaptive
open MudBlazor
open IcedTasks
open Fun.Result
open Fun.Blazor
open Brainloop.Db


[<RequireQualifiedAccess>]
type LoopCategoryTreeItem =
    | LoopCategory of LoopCategory
    | Loop of Loop

type LoopCategoryTree =

    static member private CategoryDialog(category: LoopCategory, onClose, ?onUpdated: LoopCategory -> unit) =
        html.inject (fun (hook: IComponentHook, dbService: IDbService, dialog: IDialogService) ->
            let categoryForm = hook.UseAdaptiveForm(category)

            let save () = task {
                try
                    let category = categoryForm.GetValue()
                    if category.Id = 0 then
                        let! id = dbService.DbContext.Insert<LoopCategory>().AppendData(category).ExecuteIdentityAsync()
                        onUpdated |> Option.iter (fun f -> f { category with Id = int id })
                    else
                        dbService.DbContext.Update<LoopCategory>().SetSource(category).ExecuteAffrowsAsync() |> ignore
                        onUpdated |> Option.iter (fun f -> f category)
                    onClose ()
                with ex ->
                    dialog.ShowMessage("Error saving category", ex.Message, severity = Severity.Error)
            }

            MudDialog'' {
                Header "Loop Category" onClose
                DialogContent(
                    adapt {
                        let! binding = categoryForm.UseField(fun x -> x.Name)
                        MudTextField'' {
                            Label "Name"
                            Value' binding
                            AutoFocus
                            OnKeyUp(fun e -> task { if e.Key = "Enter" then do! save () })
                        }
                    }
                )
                DialogActions [|
                    adapt {
                        let! hasChanges = categoryForm.UseHasChanges()
                        MudButton'' {
                            Color Color.Primary
                            Variant Variant.Filled
                            Disabled(not hasChanges)
                            OnClick(ignore >> save)
                            "Save"
                        }
                    }
                |]
            }
        )


    static member Create(?rootCategoryId: int, ?onItemSelected: LoopCategoryTreeItem -> unit, ?ignoreLoops: bool) =
        html.inject (fun (dbService: IDbService, dialogService: IDialogService) ->
            let refreshCount = cval 0
            let ignoreLoops = defaultArg ignoreLoops false

            let loadCategories (parentId) = task {
                let! categories =
                    dbService.DbContext.Select<LoopCategory>().Where((fun (x: LoopCategory) -> x.ParentId = parentId)).ToListAsync()
                    |> Task.map (
                        Seq.sortBy _.Name >> Seq.map (fun x -> TreeItemData(Value = LoopCategoryTreeItem.LoopCategory x, Expandable = true))
                    )

                let! loops =
                    if not ignoreLoops && parentId.HasValue then
                        dbService.DbContext.Select<Loop>().Where((fun (x: Loop) -> x.LoopCategoryId = parentId)).ToListAsync()
                        |> Task.map (
                            Seq.sortBy _.Description >> Seq.map (fun x -> TreeItemData(Value = LoopCategoryTreeItem.Loop x, Expandable = false))
                        )
                    else
                        Task.retn Seq.empty

                return
                    seq {
                        yield! categories
                        yield! loops
                    }
                    |> Seq.toArray
                    :> Generic.IReadOnlyCollection<_>
            }

            adapt {
                let! _ = refreshCount
                let! items = loadCategories (Option.toNullable rootCategoryId) |> AVal.ofTask [||]
                if rootCategoryId.IsNone then
                    MudButton'' {
                        FullWidth
                        StartIcon Icons.Material.Filled.Add
                        OnClick(fun _ ->
                            dialogService.Show(
                                DialogOptions(MaxWidth = MaxWidth.ExtraSmall, FullWidth = true),
                                fun ctx ->
                                    LoopCategoryTree.CategoryDialog(
                                        LoopCategory.Default,
                                        ctx.Close,
                                        onUpdated = (fun _ -> refreshCount.Publish((+) 1))
                                    )
                            )
                        )
                        "Add Root Category"
                    }
                MudTreeView'' {
                    Items items
                    ServerData(fun root -> task {
                        match root with
                        | LoopCategoryTreeItem.LoopCategory c -> return! loadCategories (Nullable c.Id)
                        | _ -> return [||]
                    })
                    SelectionMode SelectionMode.SingleSelection
                    SelectedValueChanged(fun item ->
                        match onItemSelected with
                        | Some fn -> fn item
                        | _ -> ()
                    )
                    ItemTemplate(fun item ->
                        let itemValue = cval item.Value
                        adapt {
                            let! itemValue' = itemValue
                            MudTreeViewItem'' {
                                Icon item.Icon
                                Value itemValue'
                                Items item.Children
                                CanExpand item.Expandable
                                LoadingIconColor Color.Primary
                                BodyContent(fun r -> div {
                                    style {
                                        displayFlex
                                        alignItemsCenter
                                        gap 12
                                        width "100%"
                                    }
                                    MudText'' {
                                        style { textOverflowWithMaxLines 1 }
                                        Color(
                                            match itemValue' with
                                            | LoopCategoryTreeItem.LoopCategory _ -> Color.Default
                                            | LoopCategoryTreeItem.Loop _ -> Color.Info
                                        )
                                        match itemValue' with
                                        | LoopCategoryTreeItem.LoopCategory x -> x.Name
                                        | LoopCategoryTreeItem.Loop x ->
                                            "- "
                                            x.Description
                                    }
                                    MudSpacer''
                                    match itemValue' with
                                    | LoopCategoryTreeItem.Loop _ -> ()
                                    | LoopCategoryTreeItem.LoopCategory category ->
                                        MudIconButton'' {
                                            Icon Icons.Material.Outlined.Add
                                            OnClick(fun _ ->
                                                dialogService.Show(
                                                    DialogOptions(MaxWidth = MaxWidth.ExtraSmall, FullWidth = true),
                                                    fun ctx ->
                                                        LoopCategoryTree.CategoryDialog(
                                                            {
                                                                LoopCategory.Default with
                                                                    ParentId = Nullable category.Id
                                                            },
                                                            ctx.Close,
                                                            onUpdated = (fun _ -> r.ReloadAsync() |> ignore)
                                                        )
                                                )
                                            )
                                        }
                                        MudIconButton'' {
                                            Icon Icons.Material.Outlined.Edit
                                            OnClick(fun _ ->
                                                dialogService.Show(
                                                    DialogOptions(MaxWidth = MaxWidth.ExtraSmall, FullWidth = true),
                                                    fun ctx ->
                                                        LoopCategoryTree.CategoryDialog(
                                                            category,
                                                            ctx.Close,
                                                            onUpdated = (fun x -> itemValue.Publish(LoopCategoryTreeItem.LoopCategory x))
                                                        )
                                                )
                                            )
                                        }
                                })
                            }
                        }
                    )
                }
            }
        )



    static member Dialog(onClose, ?rootCategoryId, ?ignoreLoops, ?onCategorySelected, ?onLoopSelected) = MudDialog'' {
        Header "Categories" onClose
        DialogContent(
            div {
                style {
                    height 720
                    maxHeight "calc(100vh - 200px)"
                    overflowXHidden
                    overflowYAuto
                }
                LoopCategoryTree.Create(
                    ?rootCategoryId = rootCategoryId,
                    ?ignoreLoops = ignoreLoops,
                    onItemSelected =
                        function
                        | LoopCategoryTreeItem.LoopCategory x ->
                            match onCategorySelected with
                            | Some fn ->
                                fn x
                                onClose ()
                            | _ -> ()
                        | LoopCategoryTreeItem.Loop x ->
                            match onLoopSelected with
                            | Some fn ->
                                fn x
                                onClose ()
                            | _ -> ()
                )
            }
        )
    }

    static member DialogBtn(?rootCategoryId, ?onCategorySelected, ?onLoopSelected, ?ignoreLoops, ?btnSize) =
        html.inject (fun (dialogService: IDialogService) -> MudIconButton'' {
            Size(defaultArg btnSize Size.Medium)
            Icon Icons.Material.Outlined.AccountTree
            OnClick(fun _ ->
                dialogService.Show(
                    DialogOptions(MaxWidth = MaxWidth.Small, FullWidth = true),
                    fun ctx ->
                        LoopCategoryTree.Dialog(
                            ctx.Close,
                            ?rootCategoryId = rootCategoryId,
                            ?ignoreLoops = ignoreLoops,
                            ?onCategorySelected = onCategorySelected,
                            ?onLoopSelected = onLoopSelected
                        )
                )
            )
        })


    static member Breadcrumbs(categoryId: int) =
        html.inject (fun (hook: IComponentHook, dbService: IDbService, dialogService: IDialogService) ->
            let rec getItems (categoryId: int) = valueTask {
                match!
                    dbService.DbContext.Select<LoopCategory>().Where(fun (x: LoopCategory) -> x.Id = categoryId).FirstAsync<LoopCategory | null>()
                with
                | null -> return []
                | category ->
                    let! parentItems =
                        if category.ParentId.HasValue then
                            getItems category.ParentId.Value
                        else
                            ValueTask.singleton []
                    return [ yield! parentItems; category ]
            }

            adapt {
                let! items = getItems categoryId |> AVal.ofValueTask []
                div {
                    style {
                        displayFlex
                        gap 4
                        alignItemsCenter
                        flexWrapWrap
                    }
                    for item in items do
                        MudLink'' {
                            style { textTransformNone }
                            Color Color.Default
                            OnClick(fun _ ->
                                dialogService.Show(
                                    DialogOptions(MaxWidth = MaxWidth.Small, FullWidth = true),
                                    fun ctx ->
                                        LoopCategoryTree.Dialog(
                                            ctx.Close,
                                            rootCategoryId = item.Id,
                                            onLoopSelected = (fun loop -> hook.ToggleLoop(loop.Id, false) |> ignore)
                                        )
                                )
                            )
                            item.Name
                        }
                        "/"
                }
            }
        )
