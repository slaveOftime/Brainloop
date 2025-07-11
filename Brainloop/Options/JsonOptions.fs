namespace System.Text.Json

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open System.Text.Unicode
open System.Text.Encodings.Web


[<RequireQualifiedAccess>]
module JsonSerializerOptions =

    let createDefault () =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)

        JsonFSharpOptions.Default().WithAllowNullFields().WithUnionUnwrapFieldlessTags().AddToJsonSerializerOptions(options)

        options.PropertyNameCaseInsensitive <- true
        options.MaxDepth <- 200
        options.ReferenceHandler <- ReferenceHandler.IgnoreCycles
        options.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        options.Encoder <- JavaScriptEncoder.Create UnicodeRanges.All
        options.WriteIndented <- true
        options


[<AutoOpen>]
module JsonUtils =

    let toJson x = JsonSerializer.Serialize(x, JsonSerializerOptions.createDefault ())

    let fromJson<'T> (json: string) =
        match JsonSerializer.Deserialize<'T>(json, JsonSerializerOptions.createDefault ()) with
        | null -> ValueNone
        | x -> ValueSome x


    type JsonSerializer with

        // For any json string, we try to parse it to JsonNode for better formatting
        static member Prettier(input: obj | null) : string | null =
            let rec loop (input: obj | null) =
                match input with
                | :? JsonNode as node ->
                    match node with
                    | :? JsonObject as obj ->
                        for KeyValue(name, property) in obj do
                            obj[name] <- loop property
                        obj :> (JsonNode | null)

                    | :? JsonArray as array ->
                        for i in 0 .. array.Count - 1 do
                            array[i] <-
                                match loop (array[i]) with
                                | null -> null :> (JsonNode | null)
                                | x -> x.DeepClone()
                        array

                    | _ when node.GetValueKind() = JsonValueKind.String -> node.ToString() |> loop

                    | x -> x

                | :? string as x ->
                    try
                        match JsonNode.Parse(x) with
                        | null -> JsonValue.Create(x)
                        | node -> loop node
                    with _ ->
                        JsonValue.Create(x)

                | :? JsonElement as x when x.ValueKind = JsonValueKind.String -> loop (x.GetString())

                | x -> JsonSerializer.Serialize(x, JsonSerializerOptions.createDefault ()) |> loop

            match loop input with
            | null -> null
            | node ->
                let jsonString = node.ToJsonString(JsonSerializerOptions.createDefault ())
                let isText = not (String.IsNullOrEmpty jsonString) && jsonString.StartsWith("\"") && jsonString.EndsWith("\"")

                if isText then
                    jsonString
                        .Substring(1, jsonString.Length - 2)
                        .Replace("\\r\\n", "\n")
                        .Replace("\\r", "\n")
                        .Replace("\\n", "\n")
                        .Replace("\\\"", "\"")
                        .Replace("\\\\", "\\")
                else
                    jsonString
                |> fun x -> x.Normalize()
