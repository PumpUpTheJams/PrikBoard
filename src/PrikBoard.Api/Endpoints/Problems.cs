using Microsoft.AspNetCore.Http.HttpResults;

namespace PrikBoard.Api.Endpoints;

/// <summary>Error answers that several endpoints give.</summary>
internal static class Problems
{
    /// <summary>A 400 about one field.</summary>
    public static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>A 400 when only one of X and Y is given; null when both or neither are.</summary>
    public static ValidationProblem? XWithoutY(int? x, int? y) =>
        x is null != y is null ? Invalid(x is null ? "x" : "y", "Give X and Y together.") : null;

    /// <summary>
    /// A 400 for a drop's bucketId or loose (where the pointer was in the web app) without X and Y, or both at once; null
    /// when they are fine. With <paramref name="bucketAlone"/>, a bucketId without X and Y is fine (a card put in a free spot).
    /// </summary>
    public static ValidationProblem? Drop(Guid? bucketId, bool? loose, bool placed, bool bucketAlone = false)
    {
        if (loose == true && bucketId is not null) return Invalid("loose", "Give either a bucketId or loose, not both.");
        if (!placed && loose == true) return Invalid("loose", "Give loose together with X and Y.");
        if (!placed && bucketId is not null && !bucketAlone) return Invalid("bucketId", "Give bucketId together with X and Y.");
        return null;
    }

    public static ValidationProblem CardNotOnBoard() => Invalid("cardId", "The card does not exist on this board.");

    /// <summary>A 403 for a token limited to chosen boards, which can't <paramref name="cannot"/> (create new ones).</summary>
    public static ProblemHttpResult LimitedToken(string cannot) => TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden,
        title: "Limited access token", detail: $"This access token is limited to chosen boards, so it cannot {cannot}.");

    /// <summary>A 409 for restoring something that is not deleted.</summary>
    public static ProblemHttpResult NotDeleted(string what) => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict, title: "Not deleted", detail: $"The {what} is not deleted, so there is nothing to restore.");
}
