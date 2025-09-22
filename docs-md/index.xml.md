```xml
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom">
  <channel>
    <title>Overview on LiteDB :: A .NET embedded NoSQL database</title>
    <link>/docs/</link>
    <description>Recent content in Overview on LiteDB :: A .NET embedded NoSQL database</description>
    <generator>Hugo -- gohugo.io</generator>
    <language>en-us</language>
    <lastBuildDate>Wed, 28 Nov 2018 15:14:39 +1000</lastBuildDate>
    
	<atom:link href="/docs/index.xml" rel="self" type="application/rss+xml" />
    
    
    <item>
      <title>Getting Started</title>
      <link>/docs/getting-started/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/getting-started/</guid>
      <description>LiteDB is a simple, fast and lightweight embedded .NET document database. LiteDB was inspired by the MongoDB database and its API is very similar to the official MongoDB .NET API.
How to install LiteDB is a serverless database, so there is no installation. Just copy LiteDB.dll into your Bin folder and add it as Reference. Or, if you prefer, you can install via NuGet: Install-Package LiteDB. If you are running in a web environment, make sure that your IIS user has write permission to the data folder.</description>
    </item>
    
    <item>
      <title>Data Structure</title>
      <link>/docs/data-structure/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/data-structure/</guid>
      <description>LiteDB stores data as documents, which are JSON-like objects containing key-value pairs. Documents are a schema-less data structure. Each document stores both its data and its structure.
{ _id: 1, name: { first: &amp;#34;John&amp;#34;, last: &amp;#34;Doe&amp;#34; }, age: 37, salary: 3456.0, createdDate: { $date: &amp;#34;2014-10-30T00:00:00.00Z&amp;#34; }, phones: [&amp;#34;8000-0000&amp;#34;, &amp;#34;9000-0000&amp;#34;] }  _id contains document primary key - a unique value in collection name contains an embedded document with first and last fields age contains a Int32 value salary contains a Double value createDate contains a DateTime value phones contains an array of String  LiteDB stores documents in collections.</description>
    </item>
    
    <item>
      <title>Object Mapping</title>
      <link>/docs/object-mapping/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/object-mapping/</guid>
      <description>The LiteDB mapper converts POCO classes documents. When you get a ILiteCollection&amp;lt;T&amp;gt; instance from LiteDatabase.GetCollection&amp;lt;T&amp;gt;, T will be your document type. If T is not a BsonDocument, LiteDB internally maps your class to BsonDocument. To do this, LiteDB uses the BsonMapper class:
// Simple strongly-typed document public class Customer { public ObjectId CustomerId { get; set; } public string Name { get; set; } public DateTime CreateDate { get; set; } public List&amp;lt;Phone&amp;gt; Phones { get; set; } public bool IsActive { get; set; } } var typedCustomerCollection = db.</description>
    </item>
    
    <item>
      <title>Collections</title>
      <link>/docs/collections/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/collections/</guid>
      <description>Documents are stored and organized in collections. LiteCollection is a generic class that is used to manage collections in LiteDB. Each collection must have a unique name:
 Contains only letters, numbers and _ Collection names are case insensitive Collection names starting with _ are reserved for internal storage use Collection names starting with $ are reserved for internal system/virtual collections  The total size of all the collections names in a database is limited to 8000 bytes.</description>
    </item>
    
    <item>
      <title>BsonDocument</title>
      <link>/docs/bsondocument/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/bsondocument/</guid>
      <description>The BsonDocument class is LiteDB&amp;rsquo;s implementation of documents. Internally, a BsonDocument stores key-value pairs in a Dictionary&amp;lt;string, BsonValue&amp;gt;.
var customer = new BsonDocument(); customer[&amp;#34;_id&amp;#34;] = ObjectId.NewObjectId(); customer[&amp;#34;Name&amp;#34;] = &amp;#34;John Doe&amp;#34;; customer[&amp;#34;CreateDate&amp;#34;] = DateTime.Now; customer[&amp;#34;Phones&amp;#34;] = new BsonArray { &amp;#34;8000-0000&amp;#34;, &amp;#34;9000-000&amp;#34; }; customer[&amp;#34;IsActive&amp;#34;] = true; customer[&amp;#34;IsAdmin&amp;#34;] = new BsonValue(true); customer[&amp;#34;Address&amp;#34;] = new BsonDocument { [&amp;#34;Street&amp;#34;] = &amp;#34;Av. Protasio Alves&amp;#34; }; customer[&amp;#34;Address&amp;#34;][&amp;#34;Number&amp;#34;] = &amp;#34;1331&amp;#34;; LiteDB supports documents up to 16MB after BSON serialization.</description>
    </item>
    
    <item>
      <title>Expressions</title>
      <link>/docs/expressions/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/expressions/</guid>
      <description>Expressions are path or formulas to access and modify the data inside a document. Based on the concept of JSON path (http://goessner.net/articles/JsonPath/), LiteDB supports a similar syntax to navigate inside a document.
In previous versons, LiteDB used lambda expressions directly on objects. This was very flexible, but also had poor perfomance. LiteDB v5 uses BsonExpressions, which are expressions that can be directly applied to a BsonDocument.
BsonExpressions can either be used natively (there is an implicit conversion between string and BsonExpression) or by mapping a lambda expression (methods that take a lambda expression do this automatically).</description>
    </item>
    
    <item>
      <title>DbRef</title>
      <link>/docs/dbref/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/dbref/</guid>
      <description>LiteDB is a document database, so there is no JOIN between collections. You can use embedded documents (sub-documents) or create a reference between collections. To create a reference you can use [BsonRef] attribute or use theDbRef method from the fluent API mapper.
Mapping a reference on database initialization public class Customer { public int CustomerId { get; set; } public string Name { get; set; } } public class Order { public int OrderId { get; set; } public Customer Customer { get; set; } } If no custom mapping is created, when you save an Order, Customer is saved as an embedded document with no link to any other collection.</description>
    </item>
    
    <item>
      <title>Connection String</title>
      <link>/docs/connection-string/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/connection-string/</guid>
      <description>LiteDatabase can be initialized using a string connection, with key1=value1; key2=value2; ... syntax. If there is no = in your connection string, LiteDB assume that your connection string contains only the Filename. Values can be quoted (&amp;quot; or &#39;) if they contain special characters (like ; or =). Keys and values are case-insensitive.
Options    Key Type Description Default value     Filename string Full or relative path to the datafile.</description>
    </item>
    
    <item>
      <title>FileStorage</title>
      <link>/docs/filestorage/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/filestorage/</guid>
      <description>To keep its memory profile slim, LiteDB limits the size of a documents to 1MB. For most documents, this is plenty. However, 1MB is too small for a useful file storage. For this reason, LiteDB implements FileStorage, a custom collection to store files and streams.
FileStorage uses two special collections:
 The first collection stores file references and metadata only (by default it is called _files)  { _id: &amp;#34;my-photo&amp;#34;, filename: &amp;#34;my-photo.</description>
    </item>
    
    <item>
      <title>Indexes</title>
      <link>/docs/indexes/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/indexes/</guid>
      <description>LiteDB improves search performance by using indexes on document fields or expressions. Each index storess the value of a specific expression ordered by the value (and type). Without an index, LiteDB must execute a query using a full document scan. Full document scans are inefficient because LiteDB must deserialize every document in the collection.
Index Implementation Indexes in LiteDB are implemented using Skip lists. Skip lists are double linked sorted list with up to 32 levels.</description>
    </item>
    
    <item>
      <title>Encryption</title>
      <link>/docs/encryption/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/encryption/</guid>
      <description>LiteDB uses salted AES (as defined by RFC 2898) as its encryption. This is implemented by the Rfc2898DeriveBytes class.
The Aes object used for cryptography is initialized with PaddingMode.None and CipherMode.ECB.
The password for an encrypted datafile is defined in the connection string (for more info, check Connection String). The password can only be changed or removed by rebuilding the datafile (for more info, check Rebuild Options in Pragmas).</description>
    </item>
    
    <item>
      <title>Pragmas</title>
      <link>/docs/pragmas/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/pragmas/</guid>
      <description>In LiteDB v5, pragmas are variables that can alter the behavior of a datafile. They are stored in the header of the datafile.
   Name Read-only Data type Description Default value     USER_VERSION no int Reserved for version control by the user. Does not affect the behavior of the datafile. 0   COLLATION yes (can be changed with a rebuild) string (internally stored as int) Check Collation.</description>
    </item>
    
    <item>
      <title>Collation</title>
      <link>/docs/collation/</link>
      <pubDate>Mon, 01 Jan 0001 00:00:00 +0000</pubDate>
      
      <guid>/docs/collation/</guid>
      <description>A collation is a special pragma (for more info, see Pragmas) that allows users to specify a culture and string compare options for a datafile.
Collation is a read-only pragma and can only be changed with a rebuild.
A collation is specified with the format CultureName/CompareOption1[,CompareOptionN]. For more info about compare options, check the .NET documentation.
Datafiles are always created with CultureInfo.CurrentCulture as their culture and with IgnoreCase as the compare option.</description>
    </item>
    
  </channel>
</rss>
```
