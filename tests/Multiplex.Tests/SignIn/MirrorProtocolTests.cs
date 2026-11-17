using System.Text;
using Multiplex.Web.SignIn;

namespace Multiplex.Tests.SignIn;

public sealed class MirrorProtocolTests
{
    private static MirrorInput? Parse(string json) => MirrorProtocol.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parses_each_input_kind()
    {
        Assert.Equal(new TapInput(0.25, 0.5), Parse("""{"t":"tap","x":0.25,"y":0.5}"""));
        Assert.Equal(new ScrollInput(0.5, 0.5, 0, 0.3), Parse("""{"t":"scroll","x":0.5,"y":0.5,"dy":0.3}"""));
        Assert.Equal(new TextInput("hello"), Parse("""{"t":"text","v":"hello"}"""));
        Assert.Equal(new KeyInput("Enter"), Parse("""{"t":"key","k":"Enter"}"""));
        Assert.IsType<BackInput>(Parse("""{"t":"back"}"""));
        Assert.IsType<ReloadInput>(Parse("""{"t":"reload"}"""));
        Assert.Equal(new ViewportInput(390, 600), Parse("""{"t":"viewport","w":390,"h":600}"""));
    }

    [Theory]
    [InlineData("""{"t":"tap","x":1.5,"y":0.5}""")]       // outside the frame
    [InlineData("""{"t":"tap","x":"0.5","y":0.5}""")]     // not a number
    [InlineData("""{"t":"key","k":"F12"}""")]             // not an allowed key
    [InlineData("""{"t":"key","k":"Control+w"}""")]
    [InlineData("""{"t":"text","v":""}""")]
    [InlineData("""{"t":"navigate","url":"https://evil.example"}""")]
    [InlineData("""{"t":"eval","js":"alert(1)"}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Rejects_anything_else(string json)
    {
        Assert.Null(Parse(json));
    }

    [Fact]
    public void Rejects_overlong_text_and_clamps_scrolls()
    {
        Assert.Null(Parse($$"""{"t":"text","v":"{{new string('a', MirrorProtocol.MaxTextLength + 1)}}"}"""));
        Assert.Equal(new ScrollInput(0, 0, 0, 2), Parse("""{"t":"scroll","x":0,"y":0,"dy":99}"""));
    }
}
