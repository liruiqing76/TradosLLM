using System.Net;

namespace TradosToolkit.TranslationProvider
{
    /// <summary>
    /// API Key 凭据，存入 ITranslationProviderCredentialStore（按 URI 索引）。
    /// </summary>
    public class ToolkitCredential
    {
        public string ApiKey { get; set; }

        public NetworkCredential ToNetworkCredential()
        {
            return new NetworkCredential(string.Empty, ApiKey ?? string.Empty);
        }
    }
}
