[<AutoOpen>]
module Brainloop.Loop.Stores

open Fun.Result
open Fun.Blazor
open Brainloop.Db
open Brainloop.Memory

// Share across different sessions (browser pages)
type IGlobalStore with

    member store.LoopTitles = store.CreateCMap<int64, LoadingState<string>>(nameof store.LoopTitles)

    member store.ActiveLoops = store.CreateCList(nameof store.ActiveLoops, List.empty<Loop>)

// Share only for current session (browser page)
type IShareStore with

    member store.MaxActiveLoops = store.CreateCVal(nameof store.MaxActiveLoops, 3)

    member store.CurrentLoop = store.CreateCVal(nameof store.CurrentLoop, ValueOption<Loop>.None)

    member store.SearchResults = store.CreateCList<MemorySearchResultItem>(nameof store.SearchResults)
    member store.SearchQuery = store.CreateCVal(nameof store.SearchQuery, "")

    member store.LoopsSharing = store.CreateCMap<int64, bool>(nameof store.LoopsSharing)
    member store.LoopContentsFocusing = store.CreateCMap<int64, int64>(nameof store.LoopContentsFocusing)
