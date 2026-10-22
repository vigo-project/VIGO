using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class InputDialogViewModelTests
{
    [Theory]
    [InlineData("my feature", "my-feature")]
    [InlineData("a  b\tc", "a--b-c")]
    [InlineData(" lead", "-lead")]
    [InlineData("trail ", "trail-")]
    [InlineData("no-space", "no-space")]
    [InlineData("", "")]
    public void ReplaceSpaces_turns_whitespace_into_dashes_keeping_the_length(string input, string expected)
    {
        var result = InputDialogViewModel.ReplaceSpaces(input);

        Assert.Equal(expected, result);
        Assert.Equal(input.Length, result.Length);
    }

    [Fact]
    public void Replaced_name_no_longer_hits_the_whitespace_error()
    {
        var dialog = new InputDialogViewModel("t", "ok", "name", Vigo.Core.Models.RefNames.Check)
        {
            Name = InputDialogViewModel.ReplaceSpaces("my feature"),
        };

        Assert.True(dialog.CanConfirm);
        Assert.Equal("my-feature", dialog.Value);
    }

    [Fact]
    public void Other_validations_still_apply_after_replacement()
    {
        var dialog = new InputDialogViewModel("t", "ok", "name", Vigo.Core.Models.RefNames.Check)
        {
            Name = InputDialogViewModel.ReplaceSpaces(" x"),
        };

        Assert.False(dialog.CanConfirm);
        Assert.True(dialog.HasNameError);
    }
}
