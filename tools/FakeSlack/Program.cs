namespace FakeSlack;

/// <summary>
/// A stand-in for Slack, for trying PrikBoard's Slack app without a Slack workspace (docs/slack.md, Testing). Run it,
/// point the API at it with <c>Slack__ApiUrl=http://localhost:5090/api/</c>, paste its tokens under Admin → Instance →
/// Slack app, then open http://localhost:5090 to post messages. Not top-level statements, so its <c>Program</c> doesn't
/// clash with the API's in the tests, which reference both.
/// </summary>
public static class Program
{
    public static void Main(string[] args) => FakeSlackApp.Build(args).Run();
}
