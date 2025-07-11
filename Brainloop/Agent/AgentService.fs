namespace Brainloop.Agent

open System
open Microsoft.Extensions.Caching.Memory
open IcedTasks
open Fun.Result
open Brainloop.Db


type AgentService(dbService: IDbService, memoryCache: IMemoryCache) as this =

    interface IAgentService with

        member _.GetAgents() = valueTask {
            return!
                dbService.DbContext
                    .Queryable<Agent>()
                    .IncludeMany((fun x -> x.AgentModels), ``then`` = (fun x -> x.Include(fun x -> x.Model) |> ignore))
                    .IncludeMany((fun x -> x.AgentFunctions))
                    .ToListAsync()
                |> Task.map Seq.toList
        }

        member _.GetAgentsWithCache() = valueTask {
            return! memoryCache.GetOrCreateAsync(Constants.AgentsMemoryCacheKey, fun _ -> (this :> IAgentService).GetAgents().AsTask())
        }

        member _.TryGetAgentWithCache(id) =
            (this :> IAgentService).GetAgentsWithCache() |> ValueTask.map (Seq.tryFind (fun x -> x.Id = id) >> ValueOption.ofOption)

        member _.GetTitleBuilderAgent() =
            dbService.DbContext
                .Queryable<Agent>()
                .IncludeMany((fun x -> x.AgentModels), ``then`` = (fun x -> x.Include(fun x -> x.Model) |> ignore))
                .IncludeMany((fun x -> x.AgentFunctions))
                .Where(fun (x: Agent) -> x.Type = AgentType.TitleBuilder)
                .FirstAsync<Agent | null>()
            |> ValueTask.ofTask


        member _.UpsertAgent(agent) = valueTask {
            memoryCache.Remove(Constants.AgentsMemoryCacheKey)

            let db = dbService.DbContext

            db.Transaction(fun _ ->
                use repo = db.GetRepository<Agent>()
                repo.DbContextOptions.EnableCascadeSave <- true

                let agentModels = agent.AgentModels
                let agentFunctions = agent.AgentFunctions

                let agent = repo.InsertOrUpdate({ agent with AgentModels = [||]; AgentFunctions = [||] })

                use repo = db.GetRepository<AgentModel>()
                let _ = repo.Delete(fun x -> x.AgentId = agent.Id)
                let _ = repo.Insert(agentModels |> Seq.map (fun x -> { x with Id = 0; AgentId = agent.Id }))

                if box agentFunctions <> null then
                    use repo = db.GetRepository<AgentFunction>()
                    let _ = repo.Delete(fun x -> x.AgentId = agent.Id)
                    let _ = repo.Insert(agentFunctions |> Seq.map (fun x -> { x with Id = 0; AgentId = agent.Id }))
                    ()
            )
        }

        member _.DeleteAgent(id) = valueTask {
            memoryCache.Remove(Constants.AgentsMemoryCacheKey)
            let db = dbService.DbContext
            db.Transaction(fun () ->
                db.Delete<Agent>().Where(fun (x: Agent) -> x.Id = id).ExecuteAffrows() |> ignore
                db.Delete<AgentModel>().Where(fun (x: AgentModel) -> x.AgentId = id).ExecuteAffrows() |> ignore
                db.Delete<AgentFunction>().Where(fun (x: AgentFunction) -> x.AgentId = id).ExecuteAffrows() |> ignore
            )
        }

        member _.UpdateUsedTime(id) = valueTask {
            let db = dbService.DbContext
            match! db.Update<Agent>(id).Set((fun (x: Agent) -> x.LastUsedAt), DateTime.Now).ExecuteAffrowsAsync() with
            | 1 -> ()
            | _ -> failwith "Failed to update agent"
        }


        member _.IsFunctionInWhitelist(agentId, functionName) =
            dbService.DbContext
                .Queryable<AgentFunctionWhitelist>()
                .Where(fun (x: AgentFunctionWhitelist) -> x.AgentId = agentId && x.FunctionName = functionName)
                .AnyAsync()
            |> ValueTask.ofTask

        member _.AddFunctionIntoWhitelist(agentId, functionName) = valueTask {
            match!
                dbService.DbContext
                    .Insert<AgentFunctionWhitelist>(
                        {
                            Id = 0
                            AgentId = agentId
                            FunctionName = functionName
                            CreatedAt = DateTime.Now
                        }
                    )
                    .ExecuteAffrowsAsync()
            with
            | 1 -> ()
            | _ -> failwith "Failed to add function in white list"
        }

        member _.RemoveFunctionFromWhitelist(agentId, functionName) = valueTask {
            match!
                dbService.DbContext
                    .Delete<AgentFunctionWhitelist>()
                    .Where(fun (x: AgentFunctionWhitelist) -> x.AgentId = agentId && x.FunctionName = functionName)
                    .ExecuteAffrowsAsync()
            with
            | 1 -> ()
            | _ -> failwith "Failed to remove function from white list"
        }


        member _.AddFunctionIntoSet(agentId, target) = valueTask {
            match!
                dbService.DbContext
                    .Insert<AgentFunction>(
                        {
                            Id = 0
                            AgentId = agentId
                            Target = target
                            CreatedAt = DateTime.Now
                        }
                    )
                    .ExecuteAffrowsAsync()
            with
            | 1 -> ()
            | _ -> failwith "Failed to add function in set"
        }

        member _.RemoveFunctionFromSet(agentId, target) = valueTask {
            match!
                dbService.DbContext
                    .Delete<AgentFunction>()
                    .Where(fun (x: AgentFunction) -> x.AgentId = agentId && x.Target = target)
                    .ExecuteAffrowsAsync()
            with
            | 1 -> ()
            | _ -> failwith "Failed to remove function from set"
        }
