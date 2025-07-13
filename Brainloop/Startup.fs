#nowarn "0020"

open System
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Serilog
open Blazored.LocalStorage
open MudBlazor.Services
open Brainloop.Options
open Brainloop.Db
open Brainloop.Handler
open Brainloop.Model
open Brainloop.Memory
open Brainloop.Agent
open Brainloop.Loop
open Brainloop.Notification
open Brainloop.Settings


let builder = WebApplication.CreateBuilder(Environment.GetCommandLineArgs())

builder.Services.AddOptions<AppOptions>().Bind(builder.Configuration.GetSection("AppOptions")).ValidateDataAnnotations()

let appOptions =
    match builder.Configuration.GetSection("AppOptions").Get<AppOptions>() with
    | null -> failwith "AppOptions are not defined"
    | x -> x

#if !DEBUG
builder.Services.AddSerilog(LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger())
#endif

builder.AddServiceDefaults()

builder.Services.AddRazorComponents().AddInteractiveServerComponents().AddHubOptions(fun options -> options.MaximumReceiveMessageSize <- Nullable())

builder.Services.AddFunBlazorServer()
builder.Services.AddBlazoredLocalStorage(fun options -> options.JsonSerializerOptions <- JsonSerializerOptions.createDefault ())
builder.Services.AddMudServices()
builder.Services.AddHttpClient()

builder.Services.AddMemoryCache()
builder.Services.AddHybridCache()

builder.Services.AddSingleton<IDbService, DbService>()
builder.Services.AddScoped<IModelService, ModelService>()
builder.Services.AddMemory(appOptions)
builder.Services.AddFunction(appOptions)
builder.Services.AddScoped<IAgentService, AgentService>()
builder.Services.AddScoped<IGetTextFromImageHandler, GetTextFromImageHandler>()
builder.Services.AddScoped<ILoopContentService, LoopContentService>()
builder.Services.AddScoped<ILoopService, LoopService>()
builder.Services.AddScoped<IStartChatLoopHandler, LoopService>()
builder.Services.AddScoped<IChatCompletionHandler, ChatCompletionHandler>()
builder.Services.AddScoped<IBuildTitleHandler, BuildTitleHandler>()
builder.Services.AddScoped<IRebuildMemoryHandler, RebuildMemoryHandler>()
builder.Services.AddScoped<IAddNotificationHandler, AddNotificationHandler>()
builder.Services.AddScoped<ISettingsService, SettingsService>()


let app = builder.Build()

app.MapDefaultEndpoints()

app.UseAntiforgery()

app.MapStaticAssets()
app.MapRazorComponents<Brainloop.Entry.Index>().AddInteractiveServerRenderMode()

app.MapMemoryApis()

app.Run()
