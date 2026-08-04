using FluentValidation;
using Sol.Application.Contracts.Device;

namespace Sol.Application.Features.Identity;

/// <summary>
/// Bounds the handshake payload.
/// </summary>
/// <remarks>
/// Rules are deliberately permissive on presence and strict on size. Any signal may legitimately
/// be missing — Safari 26 withholds screen metrics and hardwareConcurrency by default — but an
/// oversized value is either a bug or an attempt to bloat the stored signals blob.
/// <para>
/// Must be registered explicitly in DI. <c>AddValidatorsFromAssembly</c> scans by reflection and
/// registers nothing under AOT, silently, while passing every JIT-mode test.
/// </para>
/// </remarks>
public sealed class DeviceSignalsValidator : AbstractValidator<DeviceSignalsPayload>
{
    private const int MaxTextLength = 512;

    public DeviceSignalsValidator()
    {
        RuleFor(x => x.V).InclusiveBetween(1, 100);

        RuleFor(x => x.ClientStoredId)
            .Must(id => id is null || Guid.TryParse(id, out _))
            .WithMessage("clientStoredId must be a GUID when present.");

        When(x => x.Stable is not null, () =>
        {
            RuleFor(x => x.Stable!.TimeZone).MaximumLength(MaxTextLength);
            RuleFor(x => x.Stable!.Platform).MaximumLength(MaxTextLength);
            RuleFor(x => x.Stable!.PrimaryLanguage).MaximumLength(MaxTextLength);
            RuleFor(x => x.Stable!.HardwareConcurrency)
                .InclusiveBetween(1, 1024).When(x => x.Stable!.HardwareConcurrency.HasValue);
            RuleFor(x => x.Stable!.DeviceMemoryGb)
                .InclusiveBetween(0.1d, 1024d).When(x => x.Stable!.DeviceMemoryGb.HasValue);
        });

        When(x => x.Volatile is not null, () =>
        {
            RuleFor(x => x.Volatile!.UserAgent).MaximumLength(MaxTextLength);
            RuleFor(x => x.Volatile!.CanvasHash).MaximumLength(MaxTextLength);
            RuleFor(x => x.Volatile!.AudioHash).MaximumLength(MaxTextLength);
            RuleFor(x => x.Volatile!.FontsHash).MaximumLength(MaxTextLength);
            RuleFor(x => x.Volatile!.Languages)
                .Must(l => l is null || l.Count <= 32)
                .WithMessage("languages must contain at most 32 entries.");
        });
    }
}
