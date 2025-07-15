# Brainloop 🧠⇄💻

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![Version](https://img.shields.io/badge/Version-0.1.0-brightgreen)](https://github.com/slaveoftime/brainloop/releases)

> Create intelligent loops that combine LLM reasoning with actionable tools to amplify your brain.

Simple but powerful patterns to get things done with LLMs:

1. Support LLM **models** from OpenAI, Ollama, Google, MistralAI, HuggingFace.
2. Integrate **tools** from build in functions, OpenApi or MCP.
3. Define any **agents**: prompt + models + tools
4. Create loops to invoke agent to do something

![brainloop screenshot](./Docs/brainloop-0.1.0%20screenshot1.png)

There are the [video links for the demo](./Docs/brainloop-0.1.0.mp4)

## How to Use

- download binaries to run directly on Windows/Mac/Linux: [release]()
    
    Unzip it and find file **Brainloop.exe** or **Brainloop.** to run it.

    Examples for configuration (appsettings.Production.json):

    Below is the default which is using sqlite for data and memory embedding:
    ```json
    {
        "urls": "http://localhost:11435",
        "AppOptions": {
            "DataDbProvider": "SqlLite",
            "DataDbConnectionString": "Data Source=brainloop.db",
            "VectorDbProvider": "SqlLite",
            "VectorDbConnectionString": "Data Source=brainloop.db",
            "VectorCollectionName": "memory"
        }
    }
    ```
    
    > For Qdrant, the "VectorDbConnectionString" can be "Endpoint=http://xxx;Key=xxx" or just "http://xxx" if it has no api key required

- docker host, from the root dir run: docker compose up -d


## 🧩 Ecosystem Integration

| Component | Supported Providers |
|-----------------|--------------------------------------|
| **Storage** | MsSqlServer, PostgreSQL, SqlLite |
| **Vector DBs** | MsSqlServer, PostgreSQL, SqlLite, Qdrant |
| **LLMs** | OpenAI, Ollama, Google, MistralAI, HuggingFace |
| **MCP** | SSE, STDIO |


## ⚒️ Build in functions
- Get current time 
- Render html string in iframe 
- Send http request 
- Search memory by natural language 
- Read uploaded document as text 
- Execute command 
- Create a task for a specific agent 
- Create a task scheduler for a specific agent
