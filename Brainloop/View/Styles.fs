[<AutoOpen>]
module Fun.Blazor.Styles

open Fun.Css
open Fun.Css.Internal

type CssBuilder with

    [<CustomOperation "textOverflowWithMaxLines">]
    member inline _.textOverflowWithMaxLines([<InlineIfLambda>] comb: CombineKeyValue, maxLines: int) =
        comb
        &&& css {
            overflowHidden
            textOverflowEllipsis
            "-webkit-line-clamp", maxLines
            "display: -webkit-box"
            "-webkit-box-orient: vertical"
        }
