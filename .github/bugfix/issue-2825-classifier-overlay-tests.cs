using System.Runtime.ExceptionServices;
using Issue_2825_FreeListRace;
using LiteDB;

namespace LiteDB.BugfixHarness.Tests;

public class Issue2825ClassifierOverlayTests
{
    private const string PrimaryMessage = "empty page must be defined as empty type";
    private const string SecondaryMessage = "page must be writable to support changes";
    private const string PrimaryStack = "   at LiteDB.Engine.Snapshot.NewPage[T](Byte pageType)";
    private const string SecondaryStack =
        "   at LiteDB.Engine.BasePage.InternalInsert(UInt16 bytesLength, Byte& index)\n" +
        "   at LiteDB.Engine.BasePage.Insert(UInt16 bytesLength, Byte& index)\n" +
        "   at LiteDB.Engine.DataPage.InsertBlock(Int32 bytesLength, Boolean extend)\n" +
        "   at LiteDB.Engine.DataService.<>c__DisplayClass4_0.<<Insert>g__source|0>d.MoveNext()\n" +
        "   at LiteDB.Engine.BufferWriter.MoveForward(Int32 count)\n" +
        "   at LiteDB.Engine.BufferWriter.Write(Byte[] buffer, Int32 offset, Int32 count)\n" +
        "   at LiteDB.Engine.BufferWriter.WriteString(String value, Boolean specs)\n" +
        "   at LiteDB.Engine.BufferWriter.WriteElement(String key, BsonValue value)\n" +
        "   at LiteDB.Engine.BufferWriter.WriteDocument(BsonDocument value, Boolean recalc)\n" +
        "   at LiteDB.Engine.DataService.Insert(BsonDocument doc)\n" +
        "   at LiteDB.Engine.LiteEngine.InsertDocument(Snapshot snapshot, BsonDocument doc)";

    [Fact]
    public void Exact_data_insert_secondary_with_primary_is_reported()
    {
        Assert.True(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            Failure(PrimaryMessage, PrimaryStack),
            Failure(SecondaryMessage, SecondaryStack))));
        Assert.True(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            Failure(PrimaryMessage, PrimaryStack),
            Failure(SecondaryMessage, SecondaryStack.Replace("\n", "\r\n")))));
    }

    [Fact]
    public void Data_insert_secondary_without_primary_is_not_proof()
    {
        Assert.False(FreeListFailureClassifier.IsReportedFailure(
            new AggregateException(Failure(SecondaryMessage, SecondaryStack))));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            Failure("another engine failure", PrimaryStack),
            Failure(SecondaryMessage, SecondaryStack))));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            Failure(PrimaryMessage, "   at Application.NewPage()"),
            Failure(SecondaryMessage, SecondaryStack))));
    }

    [Fact]
    public void Data_insert_secondary_requires_every_ordered_frame()
    {
        var primary = Failure(PrimaryMessage, PrimaryStack);
        var frames = SecondaryStack.Split('\n');
        for (var omitted = 0; omitted < frames.Length; omitted++)
        {
            var incomplete = string.Join("\n", frames.Where((_, index) => index != omitted));
            Assert.False(FreeListFailureClassifier.IsReportedFailure(
                new AggregateException(primary, Failure(SecondaryMessage, incomplete))));
        }

        for (var position = 1; position < frames.Length; position++)
        {
            var withExtraFrame = frames.ToList();
            withExtraFrame.Insert(position, "   at Application.Unrelated()");
            Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
                primary, Failure(SecondaryMessage, string.Join("\n", withExtraFrame)))));

            var withText = frames.ToList();
            withText.Insert(position, "unrelated diagnostic text");
            Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
                primary, Failure(SecondaryMessage, string.Join("\n", withText)))));
        }

        var reordered = frames.ToArray();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            primary, Failure(SecondaryMessage, string.Join("\n", reordered)))));
    }

    [Theory]
    [InlineData("BasePage.Insert(", "BasePage.InsertWrong(")]
    [InlineData("DataService.Insert(", "DataService.InsertWrong(")]
    [InlineData("LiteEngine.InsertDocument(", "LiteEngine.InsertDocumentWrong(")]
    public void Data_insert_secondary_rejects_prefixed_method_names(string frame, string changed)
    {
        var wrongStack = SecondaryStack.Replace(frame, changed);
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            Failure(PrimaryMessage, PrimaryStack),
            Failure(SecondaryMessage, wrongStack))));
    }

    [Fact]
    public void Data_insert_secondary_rejects_wrong_type_code_or_message()
    {
        var primary = Failure(PrimaryMessage, PrimaryStack);
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            primary, new InvalidOperationException(SecondaryMessage))));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            primary, ExceptionDispatchInfo.SetRemoteStackTrace(
                new LiteException(0, SecondaryMessage), SecondaryStack))));
        Assert.False(FreeListFailureClassifier.IsReportedFailure(new AggregateException(
            primary, Failure("another engine failure", SecondaryStack))));
    }

    private static Exception Failure(string message, string stack)
    {
        return ExceptionDispatchInfo.SetRemoteStackTrace(
            new LiteException(LiteException.INVALID_DATAFILE_STATE, message), stack);
    }
}
