---
title: 'FileStorage'
draft: false
weight: 8
---

LiteDB limits each root BSON document to 16MB, including its embedded documents and arrays. Documents referenced with `BsonRef` are stored separately and do not count toward the referring document's size. For files and streams that exceed this limit, LiteDB provides `FileStorage`, which stores metadata in `_files` and splits file content into documents in `_chunks`.

`FileStorage` uses two special collections:

- The `_files` collection stores file references and metadata:

```JS
{
    _id: "my-photo",
    filename: "my-photo.jpg",
    mimeType: "image/jpg",
    length: { $numberLong: "2340000" },
	chunks: 9,
    uploadDate: { $date: "2020-01-01T00:00:00Z" },
    metadata: { "key1": "value1", "key2": "value2" }
}
```

- The `_chunks` collection stores binary data in 255kB chunks:

```JS
{
    _id: { "f": "my-photo", "n": 0 },
    data: { $binary: "VHlwZSAob3Igc ... GUpIGhlcmUuLi4" }
},
{
    _id: { "f": "my-photo", "n": 1 },
    data: { $binary: "pGaGhlcmUuLi4 ... VHlwZSAob3Igc" }
},
{
   ...
}
```

`LiteStorage` contains the following methods:

- **`Upload`**: Sends a file from the hard drive or a Stream to the database, overwriting it if already exists.
- **`Download`**: Loads a file from the database and copies it to the given Stream.
- **`Delete`**: Deletes a file reference and all related data chunks.
- **`Find`**: Returns all file references matching a query. By default, searches the `_files` collection.
- **`SetMetadata`**: Updates the metadata of a file without affecting the file data. By default, updates the `_files.metadata` document.
- **`OpenRead`**: Finds a file by its ID and returns a `LiteFileStream` to read the file content.

```C#
// Get the default FileStorage
ILiteStorage<string> fileStorage = db.FileStorage;
// Get FileStorage with custom collections
ILiteStorage<string> fileStorage = db.GetStorage<string>("myFiles", "myChunks");

// Upload file from hard drive
fileStorage.Upload("photos/2014/picture-01.jpg", "C://Temp/picture-01.jpg"); // (FileID, SourcePath)
// Upload file from Stream
fileStorage.Upload("photos/2014/picture-01.jpg", "picture-01.jpg", inputStream); // (FileID, FileName, SourceStream, Metadata = null)

// Find file reference by its ID (returns null if not found)
LiteFileInfo<string> file = fileStorage.FindById("photos/2014/picture-01.jpg");

// Load and save file bytes to hard drive
file.SaveAs("C://Temp/new-picture.jpg");
// Load and copy file bytes to Stream
file.CopyTo(outputStream);

// Find all files matching pattern
IEnumerable<LiteFileInfo<string>> files = fileStorage.Find("_id LIKE 'photos/2014/%'");
// Find all files matching pattern using parameters
IEnumerable<LiteFileInfo<string>> files = fileStorage.Find("_id LIKE @0", "photos/2014/%");
```

**NOTE**: `FileStorage` does not support transactions, to avoid loading all of the file in memory before saving it to the hard drive. However, transactions are used to upload each chunk.