namespace Brainloop.Entry

open Microsoft.AspNetCore.Components.Routing
open MudBlazor
open Fun.Blazor
open Brainloop.Notification


type NavMenu =
    static member private MoreMenus(?size: Size) =
        let size' = defaultArg size Size.Medium
        MudMenu'' {
            style { alignSelfAuto }
            Icon Icons.Material.Filled.MoreVert
            Size size'
            MudMenuItem'' {
                MudNavLink'' {
                    Match NavLinkMatch.Prefix
                    Href "/agents"
                    Icon Icons.Material.Outlined.AlternateEmail
                    "Agents"
                }
            }
            MudMenuItem'' {
                MudNavLink'' {
                    Match NavLinkMatch.Prefix
                    Icon Icons.Material.Outlined.Construction
                    Href "/tools"
                    "Tools"
                }
            }
            MudMenuItem'' {
                MudNavLink'' {
                    Match NavLinkMatch.Prefix
                    Href "/models"
                    Icon Icons.Material.Outlined.Grain
                    "Models"
                }
            }
            MudMenuItem'' {
                MudNavLink'' {
                    Match NavLinkMatch.Prefix
                    Href "/settings"
                    Icon Icons.Material.Outlined.Settings
                    "Settings"
                }
            }
        }

    static member private MobileNav = nav {
        style {
            displayFlex
            alignItemsCenter
            padding 4 0
            backgroundColor "var(--mud-palette-background)"
        }
        a {
            style {
                marginLeft 12
                marginRight 12
                marginTop 6
            }
            href "/"
            MudImage'' {
                Src "favicon.png"
                Width 40
                Height 40
            }
        }
        SectionOutlet'' { SectionName Strings.NavActionsSectionName }
        NavMenu.MoreMenus()
        NotificationView.Indicator()
        SectionOutlet'' { SectionName Strings.NavActionsRightSectionName }
    }

    static member private DesktopNav =
        html.inject (fun (shareStore: IShareStore) -> adapt {
            let! showToolbar, toggleToolbar = shareStore.IsToolbarOpen.WithSetter()
            nav {
                div {
                    style {
                        displayFlex
                        alignItemsCenter
                        if showToolbar then
                            css {
                                gap 8
                                marginRight 8
                            }
                        else
                            css {
                                gap 4
                                positionFixed
                                flexDirectionColumn
                                alignItemsFlexStart
                                left 0
                                top 0
                                bottom 0
                                zIndex 100
                            }
                    }
                    a {
                        href "/"
                        style { padding 10 }
                        MudImage'' {
                            Src "favicon.png"
                            Width 30
                            Height 30
                        }
                    }
                    region {
                        if showToolbar then
                            a {
                                href "/"
                                MudText'' {
                                    Typo Typo.h5
                                    Color Color.Primary
                                    "Brainloop"
                                }
                            }
                            //MudIconButton'' {
                            //    Icon(
                            //        if showToolbar then
                            //            Icons.Material.Filled.MenuOpen
                            //        else
                            //            Icons.Material.Filled.Menu
                            //    )
                            //    OnClick(fun _ -> toggleToolbar (not showToolbar))
                            //}
                            SectionOutlet'' { SectionName Strings.NavActionsSectionName }
                            MudNavMenu'' {
                                style {
                                    displayFlex
                                    alignItemsCenter
                                    marginRight 12
                                }
                                MudNavLink'' {
                                    Match NavLinkMatch.Prefix
                                    Href "/agents"
                                    Icon Icons.Material.Outlined.AlternateEmail
                                    "Agents"
                                }
                                MudNavLink'' {
                                    Match NavLinkMatch.Prefix
                                    Icon Icons.Material.Outlined.Construction
                                    Href "/tools"
                                    "Tools"
                                }
                                MudNavLink'' {
                                    Match NavLinkMatch.Prefix
                                    Href "/models"
                                    Icon Icons.Material.Outlined.Grain
                                    "Models"
                                }
                                MudNavLink'' {
                                    Match NavLinkMatch.Prefix
                                    Href "/settings"
                                    Icon Icons.Material.Outlined.Settings
                                    "Settings"
                                }
                                NotificationView.Indicator()
                            }
                            SectionOutlet'' { SectionName Strings.NavActionsRightSectionName }
                        else
                            SectionOutlet'' { SectionName Strings.NavQuickActionsSectionName }
                            MudSpacer''
                            MudTooltip'' {
                                Arrow
                                Placement Placement.Right
                                TooltipContent "Agents"
                                MudIconButton'' {
                                    Href "/agents"
                                    Icon Icons.Material.Outlined.AlternateEmail
                                }
                            }
                            MudTooltip'' {
                                Arrow
                                Placement Placement.Right
                                TooltipContent "Tools"
                                MudIconButton'' {
                                    Icon Icons.Material.Outlined.Construction
                                    Href "/tools"
                                }
                            }
                            MudTooltip'' {
                                Arrow
                                Placement Placement.Right
                                TooltipContent "Models"
                                MudIconButton'' {
                                    Href "/models"
                                    Icon Icons.Material.Outlined.Grain
                                }
                            }
                            NotificationView.Indicator()
                            MudIconButton'' {
                                Href "/settings"
                                Icon Icons.Material.Outlined.Settings
                            }
                            //MudIconButton'' {
                            //    Icon(
                            //        if showToolbar then
                            //            Icons.Material.Filled.MenuOpen
                            //        else
                            //            Icons.Material.Filled.Menu
                            //    )
                            //    OnClick(fun _ -> toggleToolbar (not showToolbar))
                            //}
                    }
                }
            }
        })

    static member Create() = fragment {
        MudHidden'' {
            Breakpoint Breakpoint.SmAndDown
            NavMenu.DesktopNav
        }
        MudHidden'' {
            Breakpoint Breakpoint.SmAndDown
            Invert
            NavMenu.MobileNav
        }
    }
