namespace Lumi.Documents;

/// <summary>文件被安全檢查拒絕（zip bomb、項目過多等）。訊息是固定的繁中文字，可以直接給模型看。</summary>
internal sealed class DocumentRejectedException(string message) : Exception(message);
