using Microsoft.Owin;
using Microsoft.Owin.Hosting;
using Owin;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SimplestMcpHttpServerTestEver
{
    // ====================================================================
    // 1. MODELOS DE DADOS MCP (Aderência JSON-RPC 2.0)
    // ====================================================================

    /// <summary>Estrutura Base para Requisição MCP (Seguindo JSON-RPC 2.0)</summary>
    public class McpRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; }

        [JsonPropertyName("params")]
        public JsonElement Params { get; set; }
    }

    /// <summary>Estrutura Base para Resposta MCP (Sucesso)</summary>
    public class McpResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("result")]
        public object Result { get; set; }
    }

    /// <summary>Estrutura de Erro MCP</summary>
    public class McpError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("data")]
        public object Data { get; set; }
    }

    /// <summary>Estrutura de Resposta MCP (Erro)</summary>
    public class McpErrorResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("error")]
        public McpError Error { get; set; }
    }

    // ====================================================================
    // 1.1. Modelos Auxiliares para Payloads
    // ====================================================================

    public class Tool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("inputSchema")]
        public JsonElement InputSchema { get; set; }
    }

    public class ToolsListResult
    {
        [JsonPropertyName("tools")]
        public Tool[] Tools { get; set; }
    }

    public class ToolCallParams
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("arguments")]
        public JsonElement Arguments { get; set; }

        [JsonPropertyName("_meta")]
        public JsonElement Meta { get; set; }
    }

    public class ExecuteResultPayload
    {
        [JsonPropertyName("output")]
        public object Output { get; set; }
    }

    // ====================================================================
    // 2. HANDLER OWIN (Lógica do Servidor MCP)
    // ====================================================================

    public class McpHandler
    {
        private readonly Tool[] _availableTools = new[]
        {
            new Tool
            {
                Name = "Somar",
                Description = "Soma dois números: num1 + num2.",
                InputSchema = JsonDocument.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""num1"": { ""type"": ""number"", ""description"": ""O primeiro número"" },
                        ""num2"": { ""type"": ""number"", ""description"": ""O segundo número"" }
                    },
                    ""required"": [""num1"", ""num2""]
                }").RootElement
            },
            new Tool
            {
                Name = "Subtrair",
                Description = "Subtrai dois números: num1 - num2.",
                 InputSchema = JsonDocument.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""num1"": { ""type"": ""number"", ""description"": ""O minuendo"" },
                        ""num2"": { ""type"": ""number"", ""description"": ""O subtraendo"" }
                    },
                    ""required"": [""num1"", ""num2""]
                }").RootElement
            }
        };

        private McpResponse CreateSuccessResponse(int id, object result) =>
            new McpResponse { Id = id, Result = result };

        private McpErrorResponse CreateErrorResponse(int id, int code, string message, object data = null) =>
            new McpErrorResponse
            {
                Id = id,
                Error = new McpError { Code = code, Message = message, Data = data }
            };

        public async Task Invoke(IOwinContext context)
        {
            // MCP Streamable-HTTP simplificado:
            // Aderência: O corpo deve ser multipart. Simplificamos para JSON.
            // Cabeçalho: Deveria ser multipart/form-data. Usamos application/json para o corpo simplificado.
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = 200;
            McpRequest requestMessage = null;

            // 1. Deserialização e Validação do JSON-RPC
            try
            {
                using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
                {
                    var jsonString = await reader.ReadToEndAsync();
                    if (string.IsNullOrWhiteSpace(jsonString))
                    {
                        await context.Response.WriteAsync(JsonSerializer.Serialize(CreateErrorResponse(-1, -32700, "Parse error: Request body is empty.")));
                        return;
                    }

                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    requestMessage = JsonSerializer.Deserialize<McpRequest>(jsonString, options);
                }

                if (requestMessage == null || requestMessage.JsonRpc != "2.0" || string.IsNullOrWhiteSpace(requestMessage.Method))
                {
                    await context.Response.WriteAsync(JsonSerializer.Serialize(CreateErrorResponse(requestMessage.Id, -32600, "Invalid Request: Missing required JSON-RPC fields (jsonrpc:\"2.0\", id, method).")));
                    return;
                }
            }
            catch (JsonException)
            {
                // Erro -32700: Parse error
                await context.Response.WriteAsync(JsonSerializer.Serialize(CreateErrorResponse(requestMessage?.Id ?? -1, -32700, "Parse error: Invalid JSON was received by the server.")));
                return;
            }
            catch (Exception ex)
            {
                // Erro -32603: Internal error
                await context.Response.WriteAsync(JsonSerializer.Serialize(CreateErrorResponse(requestMessage?.Id ?? -1, -32603, $"Internal Server Error during parsing: {ex.Message}")));
                return;
            }

            // 2. Roteamento e Processamento do Método
            object response;

            switch (requestMessage.Method)
            {
                case "initialize":
                    var initializedResponse = new
                    {
                        capabilities = new
                        {
                            tools = new
                            {
                                listChanged = false
                            }
                        },
                        protocolVersion = "2025-06-18",
                        serverInfo = new
                        {
                            name = "SimplestMcpHttpServerTestEver",
                            version = "0.0.1-rc1"
                        },
                    };
                    response = CreateSuccessResponse(requestMessage.Id, initializedResponse);
                    break;

                case "tools/list":
                    var toolsListResult = new ToolsListResult { Tools = _availableTools };
                    response = CreateSuccessResponse(requestMessage.Id, toolsListResult);
                    break;

                case "execute":
                case "tools/call":
                    response = HandleExecute(requestMessage);
                    break;

                default:
                    // Erro -32601: Method not found
                    response = CreateErrorResponse(requestMessage.Id, -32601, $"Method not found: {requestMessage.Method}");
                    break;
            }

            // 3. Serialização da Resposta
            var jsonResponse = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = false });
            await context.Response.WriteAsync(jsonResponse);
        }

        private object HandleExecute(McpRequest requestMessage)
        {
            ToolCallParams toolCallParams;
            try
            {
                // 1. Deserializar o objeto "params" da requisição
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                toolCallParams = JsonSerializer.Deserialize<ToolCallParams>(requestMessage.Params.GetRawText(), options);
            }
            catch (JsonException)
            {
                return CreateErrorResponse(requestMessage.Id, -32602, "Invalid params: Parameters for 'tools/call' are malformed.");
            }

            // Os argumentos vêm de toolCallParams.Arguments
            JsonElement toolArgs = toolCallParams.Arguments;
            double num1, num2;

            try
            {
                // 2. Tentar extrair os argumentos numéricos do campo 'Arguments'
                if (toolArgs.TryGetProperty("num1", out var n1) && toolArgs.TryGetProperty("num2", out var n2))
                {
                    num1 = n1.GetDouble();
                    num2 = n2.GetDouble();
                }
                else
                {
                    return CreateErrorResponse(requestMessage.Id, -32602, "Invalid params: Os parâmetros 'num1' e 'num2' são obrigatórios.");
                }
            }
            catch
            {
                return CreateErrorResponse(requestMessage.Id, -32602, "Invalid params: Os parâmetros 'num1' e 'num2' devem ser numéricos.");
            }

            // 3. Execução da ferramenta
            string toolName = toolCallParams.Name;
            double finalResult;

            switch (toolName)
            {
                case "Somar":
                    finalResult = num1 + num2;
                    break;

                case "Subtrair":
                    finalResult = num1 - num2;
                    break;

                default:
                    return CreateErrorResponse(requestMessage.Id, -32601, $"Method not found: Ferramenta '{toolName}' não encontrada.");
            }

            var executionResult = new ExecuteResultPayload { Output = finalResult };
            return CreateSuccessResponse(requestMessage.Id, executionResult);
        }
    }

    // ====================================================================
    // 3. CONFIGURAÇÃO OWIN E EXECUÇÃO (Startup.cs + Program.cs)
    // ====================================================================

    public class Startup
    {
        // OWIN usa o nome "Configuration" por convenção
        public void Configuration(IAppBuilder app)
        {
            // Adiciona o manipulador MCP ao pipeline
            app.Use((context, next) => new McpHandler().Invoke(context));
        }
    }

    internal class Program
    {
        private static void Main(string[] args)
        {
            string baseUri = "http://localhost:9000/mcp";

            try
            {
                // Inicia o servidor OWIN Self-Host, usando a classe Startup
                using (WebApp.Start<Startup>(baseUri))
                {
                    Console.WriteLine($"🚀 Servidor MCP (POC) rodando em {baseUri}");
                    Console.WriteLine("Aguardando requisições do MCPInspector...");
                    Console.WriteLine("Pressione [Enter] para sair.");
                    Console.ReadLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao iniciar o servidor. Verifique se a porta 9000 está livre ou se você tem permissão (ex: usar 'netsh http add urlacl'): {ex.Message}");
            }
        }
    }
}