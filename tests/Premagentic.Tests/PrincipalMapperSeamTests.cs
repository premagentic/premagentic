using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// The principal mapper seam where it is registered and where a start speaks
/// of it: an extension registers one at most, the built-ins bring none, and a
/// deployment whose extensions sign people in with no mapper warns, at every
/// start, that the groups they report mean nothing here; one with a mapper
/// names it in a note. No database.
/// </summary>
public sealed class PrincipalMapperSeamTests
{
    private sealed class NamedMapper(string name) : IPrincipalMapper
    {
        public string Name => name;

        public Task<IReadOnlyDictionary<string, Guid>> MapAsync(PrincipalMapRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(new Dictionary<string, Guid>());
    }

    [Fact]
    public void The_built_ins_bring_no_principal_mapper()
    {
        Assert.Null(ExtensionHost.BuiltIn.PrincipalMapper);
        Assert.Null(ExtensionHost.Load(null, []).PrincipalMapper);
        Assert.Equal(1, SeamVersions.Of(SeamVersions.PrincipalsName));
        Assert.Contains(SeamVersions.PrincipalsName, SeamVersions.Names);
    }

    [Fact]
    public void An_extension_registers_one_principal_mapper_at_most()
    {
        var registrations = new ExtensionRegistrations();
        Assert.True(registrations.IsEmpty);

        registrations.AddPrincipalMapper(new NamedMapper("first"));
        Assert.False(registrations.IsEmpty, "an extension that registers only a mapper registered something");
        Assert.Equal("first", registrations.PrincipalMapper?.Name);

        var twice = Assert.Throws<ArgumentException>(() => registrations.AddPrincipalMapper(new NamedMapper("second")));
        Assert.Equal("This extension registered two principal mappers, 'first' and 'second'. A deployment has one.", twice.Message);
        Assert.Equal("first", registrations.PrincipalMapper?.Name);
    }

    [Fact]
    public void A_principal_mapper_is_named_as_a_chunker_is()
    {
        var registrations = new ExtensionRegistrations();

        Assert.Throws<ArgumentException>(() => registrations.AddPrincipalMapper(new NamedMapper("no spaces here")));
        Assert.Throws<ArgumentException>(() => registrations.AddPrincipalMapper(new NamedMapper("")));
        Assert.Null(registrations.PrincipalMapper);

        // The control: a name shaped like a chunker's is taken.
        registrations.AddPrincipalMapper(new NamedMapper("directory-groups"));
        Assert.Equal("directory-groups", registrations.PrincipalMapper?.Name);
    }

    [Fact]
    public void A_start_with_an_extensions_way_of_signing_in_and_no_mapper_warns_that_its_groups_mean_nothing()
    {
        Assert.Equal(
            "People can sign in to this deployment through an extension: directory. Each is asked after every " +
            "built-in way, and reaches only accounts an administrator made. No principal mapper is loaded, so any " +
            "group they report means nothing here and is ignored.",
            ExtensionHosting.SignInLine(["directory"], principalMapper: null));
    }

    [Fact]
    public void A_start_with_a_mapper_notes_its_name_and_warns_of_nothing_meaning_nothing()
    {
        Assert.DoesNotContain(ExtensionHosting.NoMapperSentence,
            ExtensionHosting.SignInLine(["directory"], principalMapper: "directory-groups"), StringComparison.Ordinal);
        Assert.Equal(
            "What groups and principals from outside mean here is decided by an extension, the principal mapper " +
            "directory-groups. It can map them only into live groups an administrator made.",
            ExtensionHosting.MapperLine("directory-groups"));
    }

    [Fact]
    public void A_start_with_neither_says_nothing_of_either()
    {
        // The built-in ways of signing in report no groups, so a deployment
        // with no extension has nothing to say here.
        Assert.Null(ExtensionHosting.SignInLine([], principalMapper: null));
        Assert.Null(ExtensionHosting.MapperLine(null));
    }
}
