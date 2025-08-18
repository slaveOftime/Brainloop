namespace Brainloop.Loop

open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Web
open MudBlazor
open Fun.Blazor
open Brainloop.Db


[<Route "/">]
[<Route "/loops">]
type LoopsPage(dbService: IDbService, shareStore: IShareStore) =
    inherit FunComponent()

    let createLoop (hook: IComponentHook) = task {
        let! id = dbService.DbContext.Insert<Loop>(Loop.Default).ExecuteIdentityAsync()
        do! hook.ToggleLoop(id, false)
    }

    let createBtn =
        html.injectWithNoKey (fun (hook: IComponentHook) -> fragment {
            let iconBtn = MudIconButton'' {
                Color Color.Primary
                Icon Icons.Material.Filled.Add
                OnClick(fun _ -> createLoop hook)
            }
            MudHidden'' {
                Breakpoint Breakpoint.SmAndDown
                adapt {
                    match! shareStore.IsToolbarOpen with
                    | true -> MudButton'' {
                        Color Color.Primary
                        Variant Variant.Filled
                        OnClick(fun _ -> createLoop hook)
                        "Create"
                      }
                    | _ -> iconBtn
                }
            }
            MudHidden'' {
                Breakpoint Breakpoint.SmAndDown
                Invert
                iconBtn
            }
        })

    let treeBtn =
        html.inject (fun (hook: IComponentHook) -> LoopCategoryTree.DialogBtn(onLoopSelected = fun l -> hook.ToggleLoop(l.Id, false) |> ignore))

    override _.Render() = fragment {
        PageTitle'' { "Brainloop" }
        SectionContent'' {
            SectionName Strings.NavActionsSectionName
            LoopSearcher.Create()
            treeBtn
            MudSpacer''
        }
        SectionContent'' {
            SectionName Strings.NavActionsRightSectionName
            createBtn
        }
        SectionContent'' {
            SectionName Strings.NavQuickActionsSectionName
            LoopSearcher.Create(iconOnly = true)
            treeBtn
            MudTooltip'' {
                Arrow
                Placement Placement.Right
                TooltipContent "Create loop"
                createBtn
            }
        }
    // The main content is always display in MainLayout to avoid rerender when page switch
    }
