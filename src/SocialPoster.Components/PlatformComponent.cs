using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using SocialPoster.Core;

namespace SocialPoster.Components;

/// <summary>Provides platform configuration for standalone and nested SocialPoster components.</summary>
public abstract class PlatformComponent : ComponentBase
{
    private IReadOnlyCollection<Platform>? selectionSource;
    private PlatformSelection? selection;

    /// <summary>
    /// Platforms to display and use. Null inherits the enclosing view or registered defaults;
    /// an empty collection hides all platforms.
    /// </summary>
    [Parameter] public IReadOnlyCollection<Platform>? Platforms { get; set; }

    /// <summary>The platform selection provided by the enclosing view.</summary>
    [CascadingParameter] public PlatformSelection? InheritedPlatforms { get; set; }

    /// <summary>Registered defaults for standalone components.</summary>
    [Inject] public IOptions<SocialPosterComponentOptions> ComponentOptions { get; set; } = default!;

    /// <summary>Saved platform switches for the current UI scope.</summary>
    [Inject] public PlatformPreferences Preferences { get; set; } = default!;

    /// <summary>Loads platform switches before initializing or rendering the component.</summary>
    /// <param name="parameters">The component parameters.</param>
    /// <returns>A task representing parameter processing.</returns>
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this);
        await Preferences.LoadAsync();
        await base.SetParametersAsync(ParameterView.Empty);
    }

    /// <summary>The effective selection after applying saved Accounts page switches.</summary>
    protected PlatformSelection EnabledPlatforms => Preferences.Apply(AvailablePlatforms);

    /// <summary>The platforms allowed by the host, including those switched off on Accounts.</summary>
    protected PlatformSelection AvailablePlatforms
    {
        get
        {
            if (Platforms is null && InheritedPlatforms is not null) return InheritedPlatforms;
            var source = Platforms ?? ComponentOptions.Value.Platforms;
            if (!ReferenceEquals(source, selectionSource) || selection is null || !source.Distinct().SequenceEqual(selection.Platforms))
            {
                selection = new PlatformSelection(source);
                selectionSource = source;
            }
            return selection;
        }
    }

    /// <summary>Display names of enabled platforms supported by the account finder.</summary>
    protected string SearchablePlatformNames => string.Join(" and ", EnabledPlatforms.Searchable.Select(PlatformInfo.Label));
}
