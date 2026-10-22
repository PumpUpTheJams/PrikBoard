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

    public static ValidationProblem CardNotOnBoard() => Invalid("cardId", "The card does not exist on this board.");

    /// <summary>A 409 for restoring something that is not deleted.</summary>
    public static ProblemHttpResult NotDeleted(string what) => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict, title: "Not deleted", detail: $"The {what} is not deleted, so there is nothing to restore.");
}
