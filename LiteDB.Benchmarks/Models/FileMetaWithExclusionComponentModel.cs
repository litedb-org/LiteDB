using System.ComponentModel.DataAnnotations.Schema;

namespace LiteDB.Benchmarks.Models
{
    public class FileMetaWithExclusionComponentModel : FileMetaBaseComponentModel
    {
        public FileMetaWithExclusionComponentModel()
        {
        }

        public FileMetaWithExclusionComponentModel(FileMetaBaseComponentModel fileMetaBase)
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

        [NotMapped]
        public override bool IsValid => base.IsValid;
    }
}