using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Benchmarks.Models.Generators
{
    public static class FileMetaGenerator<T> where T : FileMetaBase, new()
    {
        private static Random _random;

        private static T Generate()
        {
            var docGuid = Guid.NewGuid();
            
            var generatedFileMeta = new T
            {
                FileId = docGuid,
                Version = _random.Next(5),
                Title = $"Document-{docGuid}",
                MimeType = "application/pdf",
                IsFavorite = _random.Next(10) >= 9,
                ShouldBeShown = _random.Next(10) >= 7,
                Vectors =  Enumerable.Range(0, 128).Select(_ => (float)_random.NextDouble()).ToArray()
            };

            if (_random.Next(10) >= 5)
            {
                generatedFileMeta.ValidFrom = DateTimeOffset.Now.AddDays(-20 + _random.Next(40));
                generatedFileMeta.ValidFrom = DateTimeOffset.UtcNow.AddDays(-10 + _random.Next(40));
            }

            return generatedFileMeta;
        }

        public static List<T> GenerateList(int amountToGenerate)
        {
            _random = new Random(0);

            var generatedList = new List<T>();
            for (var i = 0; i < amountToGenerate; i++) generatedList.Add(Generate());

            foreach (var fileMeta in generatedList)
            {
                if (_random.Next(100) <= 1)
                {
                    continue;
                }

                fileMeta.ParentId = generatedList[_random.Next(amountToGenerate)].Id;
            }

            return generatedList;
        }
    }
}