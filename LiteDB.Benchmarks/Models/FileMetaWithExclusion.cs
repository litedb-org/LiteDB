namespace LiteDB.Benchmarks.Models
{
    public class FileMetaWithExclusion : FileMetaBase
    {
        public FileMetaWithExclusion()
        {
        }

        public FileMetaWithExclusion(FileMetaBase fileMetaBase)
        {
            FileId = fileMetaBase.FileId;
            ParentId = fileMetaBase.ParentId;
            Title = fileMetaBase.Title;
            MimeType = fileMetaBase.MimeType;
            Version = fileMetaBase.Version;
            ValidFrom = fileMetaBase.ValidFrom;
            ValidTo = fileMetaBase.ValidTo;
            IsFavorite = fileMetaBase.IsFavorite;
            ShouldBeShown = fileMetaBase.ShouldBeShown;
        }

        [BsonIgnore]
        public override bool IsValid => base.IsValid;
    }
}