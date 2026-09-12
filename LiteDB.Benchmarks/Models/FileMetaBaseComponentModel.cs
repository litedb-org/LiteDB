using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LiteDB.Benchmarks.Models
{
    public class FileMetaBaseComponentModel
    {
        [NotMapped]
        public const string BsonIdPropertyKey = "_id";

        [Key]
        public virtual string Id => $"{FileId}_{Version}";

        public Guid FileId { get; set; }

        public string ParentId { get; set; }

        public string Title { get; set; }

        public string MimeType { get; set; }

        public int Version { get; set; }

        public DateTimeOffset? ValidFrom { get; set; }

        public DateTimeOffset? ValidTo { get; set; }

        public bool IsFavorite { get; set; }

        public bool ShouldBeShown { get; set; }

        public float[] Vectors { get; set; }

        public virtual bool IsValid => ValidFrom == null || ValidFrom <= DateTimeOffset.UtcNow && ValidTo == null || ValidTo > DateTimeOffset.UtcNow;
    }
}