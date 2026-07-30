using FluentAssertions;
using Rambla.Generators;
using Xunit;

namespace Rambla.Tests;

public sealed class GeneratorTests
{
    // --- success cases ---

    [Fact]
    public void Generates_property_routed_through_SetField()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            namespace Demo;
            public partial class Quote : RamblaState
            {
                [State] private decimal _bid;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public decimal Bid");
        run.Generated.Should().Contain("get => this._bid;");
        run.Generated.Should().Contain("set => SetField(ref this._bid, value);");
    }

    [Fact]
    public void Strips_leading_underscore_and_pascal_cases()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private decimal _bidPrice;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public decimal BidPrice");
    }

    [Fact]
    public void Preserves_nullable_reference_type()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private string? _label;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public string? Label");
    }

    [Fact]
    public void Generates_a_property_per_state_field()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private decimal _bid;
                [State] private decimal _ask;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public decimal Bid");
        run.Generated.Should().Contain("public decimal Ask");
    }

    [Fact]
    public void Supports_generic_and_nested_containing_types()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            namespace Demo;
            public partial class Outer<T>
            {
                public partial class Inner : RamblaState
                {
                    [State] private int _count;
                }
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("partial class Outer<T>");
        run.Generated.Should().Contain("partial class Inner");
        run.Generated.Should().Contain("public int Count");
    }

    [Fact]
    public void Escapes_keyword_field_names()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            namespace Demo;
            public partial class Quote : RamblaState
            {
                [State] private int @event;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public int Event");
        run.Generated.Should().Contain("get => this.@event;");
        run.Generated.Should().Contain("set => SetField(ref this.@event, value);");
    }

    [Fact]
    public void Setter_targets_the_field_when_it_is_named_value()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            namespace Demo;
            public partial class Quote : RamblaState
            {
                [State] private int value;
            }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public int Value");
        // Without "this." the ref would bind to the setter's implicit value
        // parameter — code that compiles but never writes the field.
        run.Generated.Should().Contain("set => SetField(ref this.value, value);");
    }

    [Fact]
    public void Distinct_types_with_colliding_sanitized_hint_names_both_generate()
    {
        // Both type keys sanitize to the same characters once '.' becomes '_'.
        GeneratorRun run = Generate("""
            using Rambla;
            namespace Demo_X { public partial class Quote : RamblaState { [State] private decimal _bid; } }
            namespace Demo { public partial class X_Quote : RamblaState { [State] private decimal _ask; } }
            """);

        run.Errors.Should().BeEmpty();
        run.Generated.Should().Contain("public decimal Bid");
        run.Generated.Should().Contain("public decimal Ask");
    }

    // --- diagnostics ---

    [Fact]
    public void RMB001_when_containing_type_not_partial()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            public class Quote : RamblaState
            {
                [State] private decimal _bid;
            }
            """);

        run.HasError("RMB001").Should().BeTrue();
    }

    [Fact]
    public void RMB002_when_field_is_static()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private static decimal _bid;
            }
            """);

        run.HasError("RMB002").Should().BeTrue();
    }

    [Fact]
    public void RMB003_when_name_collides_with_existing_member()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private decimal _bid;
                public decimal Bid => 0m;
            }
            """);

        run.HasError("RMB003").Should().BeTrue();
    }

    [Fact]
    public void RMB004_when_field_is_readonly()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            public partial class Quote : RamblaState
            {
                [State] private readonly decimal _bid;
            }
            """);

        run.HasError("RMB004").Should().BeTrue();
    }

    [Fact]
    public void RMB005_when_type_does_not_derive_from_RamblaState()
    {
        GeneratorRun run = Generate("""
            using Rambla;
            public partial class Quote
            {
                [State] private decimal _bid;
            }
            """);

        run.HasError("RMB005").Should().BeTrue();
    }

    private static GeneratorRun Generate(string source)
        => GeneratorHarness.Generate(source, new StateGenerator());
}
