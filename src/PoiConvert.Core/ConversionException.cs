namespace PoiConvert.Core;

/// <summary>ユーザーにそのまま見せられる（日本語の）メッセージを持つ例外。</summary>
public sealed class ConversionException : Exception
{
    public ConversionException(string message) : base(message) { }
}
