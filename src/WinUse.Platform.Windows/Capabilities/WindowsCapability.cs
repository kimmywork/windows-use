using WinUse.Core.Capabilities;
using WinUse.Core.Errors;
using WinUse.Core.Protocol;
using WinUse.Platform.Windows.Capture;
using WinUse.Platform.Windows.Input;
using WinUse.Platform.Windows.Uia;
using WinUse.Platform.Windows.Windowing;

namespace WinUse.Platform.Windows.Capabilities;

/// <summary>
/// Windows 能力基类：统一异常 → 结构化错误映射，禁止静默成功。
/// 各能力实现同步 <see cref="Run"/>，由基类包装为 Task。
/// </summary>
internal abstract class WindowsCapability : ICapability
{
    public abstract string Id { get; }

    protected abstract CapabilityResult Run(CapabilityContext context);

    public Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(Run(context));
        }
        catch (CapabilityResultException cre)
        {
            return Task.FromResult(CapabilityResult.Failure(cre.ErrorType, cre.Message, cre.ErrorData));
        }
        catch (MissingParameterException mpe)
        {
            return Task.FromResult(CapabilityResult.Failure(
                ErrorCatalog.Standard(ErrorCatalog.InvalidParams, mpe.Message,
                    new { parameter = mpe.ParameterName })));
        }
        catch (ElementNotFoundException)
        {
            return Task.FromResult(CapabilityResult.Failure("ELEMENT_NOT_FOUND"));
        }
        catch (AmbiguousElementException aee)
        {
            return Task.FromResult(CapabilityResult.Failure("AMBIGUOUS", data: new { count = aee.Count }));
        }
        catch (PatternUnsupportedException)
        {
            return Task.FromResult(CapabilityResult.Failure("PATTERN_UNSUPPORTED"));
        }
        catch (WindowNotFoundException)
        {
            return Task.FromResult(CapabilityResult.Failure("ELEMENT_NOT_FOUND"));
        }
        catch (AmbiguousWindowException awe)
        {
            return Task.FromResult(CapabilityResult.Failure("AMBIGUOUS", data: new { count = awe.Message }));
        }
        catch (CaptureProtectedException)
        {
            return Task.FromResult(CapabilityResult.Failure("CAPTURE_PROTECTED"));
        }
        catch (UiaTimeoutException)
        {
            return Task.FromResult(CapabilityResult.Failure("TIMEOUT"));
        }
        catch (UiaBusyException)
        {
            return Task.FromResult(CapabilityResult.Failure("RATE_LIMITED"));
        }
        catch (SurrogateNotSupportedException sns)
        {
            return Task.FromResult(CapabilityResult.Failure("NOT_SUPPORTED", sns.Message));
        }
        catch (UnauthorizedAccessException uae)
        {
            return Task.FromResult(CapabilityResult.Failure("PERMISSION_DENIED", uae.Message));
        }
        catch (ArgumentException ae)
        {
            return Task.FromResult(CapabilityResult.Failure(
                ErrorCatalog.Standard(ErrorCatalog.InvalidParams, ae.Message)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CapabilityResult.Failure(
                ErrorCatalog.Standard(ErrorCatalog.InternalError, ex.Message)));
        }
    }

    protected static CapabilityResult Fail(string errorType, string? message = null, object? data = null) =>
        CapabilityResult.Failure(errorType, message, data);

    protected static CapabilityResult Ok(object? value = null) =>
        CapabilityResult.Success(value);
}

/// <summary>能力内部主动抛出的结构化错误。</summary>
internal sealed class CapabilityResultException : Exception
{
    public string ErrorType { get; }
    public object? ErrorData { get; }

    public CapabilityResultException(string errorType, string? message = null, object? data = null)
        : base(message)
    {
        ErrorType = errorType;
        ErrorData = data;
    }
}
