using FluentAssertions;
using Rambla.Generators;
using Xunit;

namespace Rambla.Tests;

/// <summary>
/// The <c>[StateCommand]</c> generator. Every success case asserts
/// <see cref="GeneratorRun.Errors"/> is empty, which means the emitted code was
/// compiled against the real <c>Rambla</c> assembly — the members it calls
/// (<c>EnsureCommand</c>, <c>MarkDirty</c>, <c>Scheduler</c>) have to exist and
/// type-check.
/// </summary>
public sealed class CommandGeneratorTests
{
    [Fact]
    public void Generates_the_command_and_its_state_from_an_async_method()
    {
        GeneratorRun run = Generate("""
            using System.Threading;
            using System.Threading.Tasks;
            using Rambla;
            namespace Demo;
            public partial class Quotes : RamblaState
            {
                [StateCommand]
                private Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public global::Rambla.AsyncStateCommand RefreshCommand");
        run.Generated.Should().Contain("public bool IsRefreshing => RefreshCommand.IsRunning;");
        run.Generated.Should().Contain("public global::System.Exception? RefreshError => RefreshCommand.Error;");
        run.Generated.Should().Contain("CancelRefreshCommand => RefreshCommand.CancelCommand;");
        run.Generated.Should().Contain("token => RefreshAsync(token),");
        run.Generated.Should().Contain("scheduler: Scheduler);");
    }

    [Fact]
    public void Wraps_a_method_that_takes_no_cancellation_token()
    {
        GeneratorRun run = Generate("""
            using System.Threading.Tasks;
            using Rambla;
            public partial class Quotes : RamblaState
            {
                [StateCommand]
                private Task LoadAsync() => Task.CompletedTask;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("_ => LoadAsync(),");
        run.Generated.Should().Contain("public bool IsLoading => LoadCommand.IsRunning;");
    }

    [Fact]
    public void Escapes_keyword_method_names()
    {
        GeneratorRun run = Generate("""
            using System.Threading.Tasks;
            using Rambla;
            namespace Demo;
            public partial class Quotes : RamblaState
            {
                [StateCommand(Name = "Fire")]
                private Task @event() => Task.CompletedTask;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("_ => @event(),");
    }

    [Fact]
    public void Passes_CancelPrevious_through_to_the_command()
    {
        GeneratorRun run = Generate("""
            using System.Threading;
            using System.Threading.Tasks;
            using Rambla;
            public partial class Search : RamblaState
            {
                [StateCommand(CancelPrevious = true)]
                private Task SearchAsync(CancellationToken token) => Task.CompletedTask;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("cancelPrevious: true,");
        run.Generated.Should().Contain("public bool IsSearching");
    }

    [Fact]
    public void Honours_explicit_name_overrides()
    {
        GeneratorRun run = Generate("""
            using System.Threading.Tasks;
            using Rambla;
            public partial class Session : RamblaState
            {
                [StateCommand(Name = "SignIn", BusyName = "IsSigningIn", ErrorName = "SignInFailure")]
                private Task LoginAsync() => Task.CompletedTask;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("AsyncStateCommand SignInCommand");
        run.Generated.Should().Contain("public bool IsSigningIn");
        run.Generated.Should().Contain("SignInFailure");
        run.Generated.Should().Contain("CancelSignInCommand");
    }

    [Fact]
    public void Generates_one_command_per_annotated_method()
    {
        GeneratorRun run = Generate("""
            using System.Threading.Tasks;
            using Rambla;
            public partial class Quotes : RamblaState
            {
                [StateCommand] private Task LoadAsync() => Task.CompletedTask;
                [StateCommand] private Task SaveAsync() => Task.CompletedTask;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("LoadCommand");
        run.Generated.Should().Contain("SaveCommand");
        run.Generated.Should().Contain("public bool IsSaving");
    }

    [Fact]
    public void Supports_generic_and_nested_containing_types()
    {
        GeneratorRun run = Generate("""
            using System.Threading.Tasks;
            using Rambla;
            namespace Demo;
            public partial class Outer<T>
            {
                public partial class Inner : RamblaState
                {
                    [StateCommand] private Task RunAsync() => Task.CompletedTask;
                }
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("partial class Outer<T>");
        run.Generated.Should().Contain("RunCommand");
    }

    [Theory]
    [InlineData("RefreshAsync", "IsRefreshing")]
    [InlineData("SaveAsync", "IsSaving")]
    [InlineData("SubmitAsync", "IsSubmitting")]
    [InlineData("StopAsync", "IsStopping")]
    [InlineData("ConnectAsync", "IsConnecting")]
    [InlineData("ExportAsync", "IsExporting")]
    [InlineData("UpdateAsync", "IsUpdating")]
    [InlineData("OpenAsync", "IsOpening")]
    [InlineData("TransferAsync", "IsTransferring")]
    public void Derives_the_busy_name_from_the_verb(string method, string expected)
    {
        GeneratorRun run = Generate($$"""
            using System.Threading.Tasks;
            using Rambla;
            public partial class Worker : RamblaState
            {
                [StateCommand] private Task {{method}}() => Task.CompletedTask;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public bool " + expected);
    }

    // --- diagnostics ---

    [Fact]
    public void RMB001_when_containing_type_not_partial()
    {
        GeneratorRun run = Generate("""
            using System.Threading.Tasks;
            using Rambla;
            public class Quotes : RamblaState
            {
                [StateCommand] private Task LoadAsync() => Task.CompletedTask;
            }
            """);

        run.HasError("RMB001").Should().BeTrue();
    }

    [Fact]
    public void RMB005_when_type_does_not_derive_from_RamblaState()
    {
        GeneratorRun run = Generate("""
            using System.Threading.Tasks;
            using Rambla;
            public partial class Quotes
            {
                [StateCommand] private Task LoadAsync() => Task.CompletedTask;
            }
            """);

        run.HasError("RMB005").Should().BeTrue();
    }

    [Fact]
    public void RMB006_when_the_method_is_static()
    {
        GeneratorRun run = Generate("""
            using System.Threading.Tasks;
            using Rambla;
            public partial class Quotes : RamblaState
            {
                [StateCommand] private static Task LoadAsync() => Task.CompletedTask;
            }
            """);

        run.HasError("RMB006").Should().BeTrue();
    }

    [Theory]
    [InlineData("private void Load() { }")]
    [InlineData("private Task<int> LoadAsync() => Task.FromResult(0);")]
    [InlineData("private System.Threading.Tasks.ValueTask LoadAsync() => default;")]
    public void RMB007_when_the_method_does_not_return_Task(string declaration)
    {
        GeneratorRun run = Generate($$"""
            using System.Threading.Tasks;
            using Rambla;
            public partial class Quotes : RamblaState
            {
                [StateCommand] {{declaration}}
            }
            """);

        run.HasError("RMB007").Should().BeTrue();
    }

    [Theory]
    [InlineData("private Task LoadAsync(int page) => Task.CompletedTask;")]
    [InlineData("private Task LoadAsync(int page, System.Threading.CancellationToken token) => Task.CompletedTask;")]
    [InlineData("private Task LoadAsync<T>() => Task.CompletedTask;")]
    public void RMB008_when_the_signature_is_unsupported(string declaration)
    {
        GeneratorRun run = Generate($$"""
            using System.Threading.Tasks;
            using Rambla;
            public partial class Quotes : RamblaState
            {
                [StateCommand] {{declaration}}
            }
            """);

        run.HasError("RMB008").Should().BeTrue();
    }

    [Fact]
    public void RMB009_when_a_generated_name_collides_with_an_existing_member()
    {
        GeneratorRun run = Generate("""
            using System.Threading.Tasks;
            using Rambla;
            public partial class Quotes : RamblaState
            {
                [StateCommand] private Task LoadAsync() => Task.CompletedTask;
                public bool IsLoading => false;
            }
            """);

        run.HasError("RMB009").Should().BeTrue();
    }

    [Fact]
    public void RMB009_when_two_commands_generate_the_same_name()
    {
        GeneratorRun run = Generate("""
            using System.Threading.Tasks;
            using Rambla;
            public partial class Quotes : RamblaState
            {
                [StateCommand] private Task LoadAsync() => Task.CompletedTask;
                [StateCommand(Name = "Load")] private Task ReloadAsync() => Task.CompletedTask;
            }
            """);

        run.HasError("RMB009").Should().BeTrue();
    }

    [Fact]
    public void Coexists_with_the_State_generator_on_the_same_type()
    {
        GeneratorRun run = GeneratorHarness.Generate(
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Rambla;
            public partial class Quotes : RamblaState
            {
                [State] private decimal _bid;

                [StateCommand(CancelPrevious = true)]
                private Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
            }
            """,
            new StateGenerator(),
            new StateCommandGenerator());

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public decimal Bid");
        run.Generated.Should().Contain("RefreshCommand");
    }

    private static GeneratorRun Generate(string source)
        => GeneratorHarness.Generate(source, new StateCommandGenerator());
}
