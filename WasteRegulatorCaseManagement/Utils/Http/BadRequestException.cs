namespace WasteRegulatorCaseManagement.Utils.Http;

/// <summary>
/// A request the caller got wrong. <see cref="Logging.ExceptionLoggingHandler"/>
/// answers it with a 400 problem of this <see cref="Title"/> and the
/// exception's message as its detail, and logs it as a warning rather than
/// as an unhandled error. The message is returned to the caller, so it must
/// be safe for them to see.
/// </summary>
public abstract class BadRequestException(string title, string message) : Exception(message)
{
	public string Title { get; } = title;
}
