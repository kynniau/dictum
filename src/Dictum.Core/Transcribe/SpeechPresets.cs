using Dictum.Llm;
using Dictum.Settings;

namespace Dictum.Transcribe;

/// <summary>
/// The cloud speech services v1 knows (spec "Cloud speech" §1, 22/10/2026): all three take the OpenAI shape, POST
/// {base_url}/audio/transcriptions, so one client serves them. They decide only which key goes where, by the clean-up
/// presets' own address rule (<see cref="CloudPresets.Match"/>): a preset's key goes to its own server and to no other,
/// SPEECH_API_KEY only to any other address. No default model is filled in: the model ids in the spec came from a page summary
/// that it says to re-read before an id goes into code, so speech.model is always the user's (the UI lane can add defaults
/// after reading the providers' pages).
/// </summary>
/// <remarks>
/// The addresses are the spec's table (provider pages read on 22/10/2026, OpenAI's and Groq's through a fetch-tool summary);
/// this lane did not re-read them. Azure OpenAI's address carries the customer's resource name, so its preset matches any
/// https:// host ending .openai.azure.com. OpenAI, Groq and a custom server get <c>Authorization: Bearer</c> (Groq's header
/// is Recalled, not read on its page); Azure OpenAI gets <c>api-key</c>.
/// </remarks>
public static class SpeechPresets
{
    /// <summary>A speech server at an address the user types: any address that is not a preset's own server.</summary>
    public static CloudPreset Custom { get; } = new("Custom server (OpenAI-compatible)", null, null, SecretStore.SpeechKey, KeyRequired: false, "the model name the server uses");

    public static IReadOnlyList<CloudPreset> All { get; } =
    [
        new("OpenAI", "https://api.openai.com/v1", null, SecretStore.OpenAiKey, KeyRequired: true, "the transcription model id"),
        // The key in an api-key header: Microsoft Learn's transcription reference (read by the lens, 22/10/2026:
        // https://learn.microsoft.com/azure/foundry/openai/reference-preview-latest#create-transcription). Not re-read by this lane.
        new("Azure OpenAI", null, null, SecretStore.AzureOpenAiKey, KeyRequired: true, "your deployment name", HostSuffix: ".openai.azure.com", KeyHeader: "api-key"),
        new("Groq", "https://api.groq.com/openai/v1", null, SecretStore.GroqKey, KeyRequired: true, "the transcription model id"),
        Custom,
    ];

    /// <summary>The speech preset whose server speech.base_url names, else <see cref="Custom"/>.</summary>
    public static CloudPreset ForAddress(string? baseUrl) => CloudPresets.Match(All, Custom, baseUrl);
}
