using TenE0.Core.Abstractions;
using TenE0.Core.Errors;

namespace TenE0.Core.Tests.Errors;

public sealed class ErrsTests
{
    private Errs CreateSubject() => new();

    [Fact]
    public void Constructor_ShouldBeValidInitially()
    {
        var errs = CreateSubject();

        errs.IsValid.Should().BeTrue();
        errs.Entries.Should().BeEmpty();
        errs.Keys.Should().BeEmpty();
    }

    [Fact]
    public void Add_SingleError_ShouldInvalid()
    {
        var errs = CreateSubject();

        errs.Add("something went wrong");

        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().HaveCount(1);
        errs.Entries[0].Message.Should().Be("something went wrong");
    }

    [Fact]
    public void Add_WithKey_ShouldPopulateKeys()
    {
        var errs = CreateSubject();

        errs.Add("invalid email", key: "Email");

        errs.Keys.Should().ContainSingle().Which.Should().Be("Email");
    }

    [Fact]
    public void Add_WithCode_ShouldPreserveCode()
    {
        var errs = CreateSubject();

        errs.Add("duplicate entry", code: "UNIQUE_VIOLATION");

        errs.Entries.Should().ContainSingle();
        errs.Entries[0].Code.Should().Be("UNIQUE_VIOLATION");
    }

    [Fact]
    public void GetFirstError_NoErrors_ReturnsNull()
    {
        var errs = CreateSubject();

        errs.GetFirstError().Should().BeNull();
    }

    [Fact]
    public void GetFirstError_WithErrors_ReturnsFirst()
    {
        var errs = CreateSubject();

        errs.Add("first error");
        errs.Add("second error");

        errs.GetFirstError().Should().Be("first error");
    }

    [Fact]
    public void Clear_ShouldResetAll()
    {
        var errs = CreateSubject();
        errs.Add("error 1", key: "A");
        errs.Add("error 2", key: "B");

        errs.Clear();

        errs.IsValid.Should().BeTrue();
        errs.Entries.Should().BeEmpty();
        errs.Keys.Should().BeEmpty();
        errs.GetFirstError().Should().BeNull();
    }

    [Fact]
    public void Keys_DuplicateKeys_ShouldReturnDistinct()
    {
        var errs = CreateSubject();

        errs.Add("error on name 1", key: "Name");
        errs.Add("error on name 2", key: "Name");
        errs.Add("error on age", key: "Age");

        errs.Keys.Should().BeEquivalentTo("Name", "Age");
        errs.Keys.Should().HaveCount(2);
    }

    [Fact]
    public void Keys_NullKey_ShouldNotAppear()
    {
        var errs = CreateSubject();

        errs.Add("general error");
        errs.Add("field error", key: "Email");

        errs.Keys.Should().ContainSingle().Which.Should().Be("Email");
    }

    [Fact]
    public void Entries_ExposesIReadOnlyList()
    {
        var errs = CreateSubject();
        errs.Add("some error", key: "X");

        // Errs.Entries 返回 IReadOnlyList<ErrorEntry> 接口类型
        IReadOnlyList<ErrorEntry> entries = errs.Entries;
        entries.Should().NotBeNull();
        entries.Should().HaveCount(1);
        entries[0].Message.Should().Be("some error");
    }
}
