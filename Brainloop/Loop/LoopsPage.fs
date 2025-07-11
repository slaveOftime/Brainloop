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

    override _.Render() = fragment {
        PageTitle'' { "Brain Loops" }
        SectionContent'' {
            SectionName Constants.NavActionsSectionName
            LoopSearcher.Create()
            MudSpacer''
        }
        SectionContent'' {
            SectionName Constants.NavActionsRightSectionName
            createBtn
        }
        SectionContent'' {
            SectionName Constants.NavQuickActionsSectionName
            LoopSearcher.Create(iconOnly = true)
            MudTooltip'' {
                Arrow
                Placement Placement.Top
                TooltipContent "Create loop"
                createBtn
            }
        }
        // The main content is always display in MainLayout to avoid rerender when page switch
    }
