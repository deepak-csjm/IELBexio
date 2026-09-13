using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace IelBexio.Ai.Gateway;

/// <summary>
/// Azure OpenAI transport. Deliberately thin: it performs the call and returns tokens, and holds no
/// policy of its own — every control lives in <see cref="AiGateway"/> and <see cref="AiGuard"/>, so
/// there is one place to audit rather than two.
/// <para>
/// The model is given no tools. That is the structural guarantee behind §14: there is no function the
/// model could invoke even if a document tried to talk it into one.
/// </para>
/// </summary>
public sealed class AzureOpenAiCompletionClient : IAiCompletionClient
{
    private readonly ChatClient _chat;
    private readonly AiOptions _options;

    public AzureOpenAiCompletionClient(IOptions<AiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("Ai:Endpoint and Ai:Deployment must be configured to use Azure OpenAI.");
        }

        var endpoint = new Uri(_options.Endpoint!);

        // Prefer managed identity; fall back to a key only when one is explicitly configured. Key-less
        // is the better production posture and costs nothing to prefer here.
        var client = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
            : new AzureOpenAIClient(endpoint, new ApiKeyCredential(_options.ApiKey));

        _chat = client.GetChatClient(_options.Deployment);
    }

    public string Model => _options.Deployment ?? "(unconfigured)";

    public async Task<AiCompletionResponse> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string jsonSchema,
        string schemaName,
        float temperature,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var chatOptions = new ChatCompletionOptions
        {
            Temperature = temperature,
            MaxOutputTokenCount = maxOutputTokens,

            // Structured output: the provider itself constrains the response to the schema. The gateway
            // still validates the result independently — belt and braces, because the schema feature is
            // a convenience, not a security control.
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: schemaName,
                jsonSchema: BinaryData.FromString(jsonSchema),
                jsonSchemaIsStrict: false),
        };

        var completion = await _chat.CompleteChatAsync(
            [
                ChatMessage.CreateSystemMessage(systemPrompt),
                ChatMessage.CreateUserMessage(userPrompt),
            ],
            chatOptions,
            cancellationToken);

        var content = completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text : string.Empty;

        return new AiCompletionResponse(
            content,
            completion.Value.Usage?.InputTokenCount ?? 0,
            completion.Value.Usage?.OutputTokenCount ?? 0);
    }
}
