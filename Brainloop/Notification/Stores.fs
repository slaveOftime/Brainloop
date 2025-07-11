[<AutoOpen>]
module Brainloop.Notification.Stores

open Fun.Blazor
open Brainloop.Db

// Share across different sessions (browser pages)
type IGlobalStore with

    member store.Notifications = store.CreateCList<Notification>(nameof store.Notifications)
