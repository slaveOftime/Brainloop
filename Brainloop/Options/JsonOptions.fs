namespace System.Text.Json

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open System.Text.Unicode
open System.Text.Encodings.Web


[<RequireQualifiedAccess>]
module JsonSerializerOptions =

    type StringBooleanConverter() =
        inherit JsonConverter<bool>()

        override _.Read(reader: byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions) =
            match reader.TokenType with
            | JsonTokenType.String ->
                let success, value = Boolean.TryParse(reader.GetString())
                if success then value else raise (JsonException("Cannot convert to bool."))
            | JsonTokenType.True -> true
            | JsonTokenType.False -> false
            | _ -> raise (JsonException("Cannot convert to bool."))

        override _.Write(writer: Utf8JsonWriter, value: bool, options: JsonSerializerOptions) = writer.WriteBooleanValue(value)

    let createDefault () =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)

        JsonFSharpOptions.Default().WithAllowNullFields().WithUnionUnwrapFieldlessTags().AddToJsonSerializerOptions(options)

        options.PropertyNameCaseInsensitive <- true
        options.MaxDepth <- 200
        options.ReferenceHandler <- ReferenceHandler.IgnoreCycles
        options.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        options.Encoder <- JavaScriptEncoder.Create UnicodeRanges.All
        options.WriteIndented <- true
        options.Converters.Add(JsonStringEnumConverter())
        options.Converters.Add(StringBooleanConverter())
        options


[<AutoOpen>]
module JsonUtils =

    let toJson x = JsonSerializer.Serialize(x, JsonSerializerOptions.createDefault ())

    let fromJson<'T> (json: string) =
        match JsonSerializer.Deserialize<'T>(json, JsonSerializerOptions.createDefault ()) with
        | null -> ValueNone
        | x -> ValueSome x


    let mergeJson (json1: string) (json2: string) =
        let rec loop (obj1: JsonObject) (obj2: JsonObject) =
            for kvp in obj2 do
                match kvp.Value, obj1.TryGetPropertyValue(kvp.Key) with
                // Object into object
                | (:? JsonObject as newValue), (true, (:? JsonObject as existingValue)) ->
                    obj1[kvp.Key] <- loop existingValue (newValue.DeepClone() :?> JsonObject)
                // Array mrege into array
                | (:? JsonArray as newArray), (true, (:? JsonArray as existingArray)) ->
                    for item in newArray do
                        existingArray.Add(item.DeepClone())
                // If the key does not exist, add it
                | _ -> obj1[kvp.Key] <- kvp.Value.DeepClone()

            obj1

        let options = JsonSerializerOptions.createDefault ()

        let mergeObj =
            loop (JsonSerializer.Deserialize<JsonObject>(json1, options)) (JsonSerializer.Deserialize<JsonObject>(json2, options))

        mergeObj.ToJsonString(options)


    type JsonSerializer with

        // For any json string, we try to parse it to JsonNode for better formatting
        static member Prettier(input: obj | null) : string | null =
            let jsonOptions = JsonSerializerOptions.createDefault ()
            jsonOptions.Encoder <- JavaScriptEncoder.UnsafeRelaxedJsonEscaping

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

                | x -> JsonSerializer.Serialize(x, jsonOptions) |> loop

            match loop input with
            | null -> null
            | node ->
                let jsonString = node.ToJsonString(jsonOptions)
                let isText = not (String.IsNullOrEmpty jsonString) && jsonString.StartsWith("\"") && jsonString.EndsWith("\"")

                if isText then
                    StringBuilder(jsonString)
                        .Remove(0, 1)
                        .Remove(jsonString.Length - 2, 1)
                        .Replace("\r\n", "\n")
                        .Replace("\\r\\n", "\n")
                        .Replace("\r", "\n")
                        .Replace("\\r", "\n")
                        .Replace("\\\\", "\\")
                        .ToString()
                else
                    jsonString
