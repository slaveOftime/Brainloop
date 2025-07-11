namespace Brainloop.Entry

open System.Reflection
open Fun.Blazor

type Routes() =
    inherit FunComponent()

    override _.Render() =
        html.scoped (
            true,
            Router'' {
                AppAssembly(Assembly.GetExecutingAssembly())
                Found(fun routeData -> RouteView'' {
                    RouteData routeData
                    DefaultLayout typeof<MainLayout>
                })
            }
        )
