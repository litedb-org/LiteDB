
# SynthetikDelusion.MemoryEngine – A Hybrid Cognitive Memory Store for AI Agents

This repository is a customized fork of the original [LiteDB](https://github.com/mbdavid/LiteDB) by Maurício David. It serves as the memory backbone for the [Synthetik Delusion](https://github.com/hurley451/synthetikdelusion) framework—a brain-inspired cognitive architecture for autonomous agents written in C#.

This enhanced fork adds **hybrid vector + graph memory**, **emotionally weighted recall**, and **context-aware memory scoping**, while preserving the full document-based NoSQL functionality of LiteDB.

> **Credit:** Full credit for the embedded NoSQL database foundation goes to [LiteDB by mbdavid](https://github.com/mbdavid/LiteDB). This fork is respectful of the original MIT license and extends the system to support synthetic cognition.

---

## ✨ Enhancements Over Base LiteDB

- 🔗 **Graph Memory Model** with embedded semantic edge relationships
- 📐 **BsonVector**: native support for `float[]` embeddings
- 🧠 **MemoryNode + MemoryEdge** constructs for storing and linking insights
- ⚖️ **Weighted Memory Recall**: recall based on similarity, recency, emotional charge, frequency, and context
- 🧳 **Scoped Memory Stores**: working memory (TTL-based), long-term memory, and cognitive unit-local memory
- 🔁 **Memory Consolidator**: promotes meaningful memories based on relevance and usage patterns
- 🧮 **Similarity and relevance queries**: cosine similarity and relevance-weighted scoring for top-K recall

---

## 📦 Project Status

This memory engine is actively used in Synthetik Delusion as the core of the agent's cognitive memory loop.

It is not a general-purpose database fork—but rather a **foundational cognitive substrate** tailored for AI reasoning, experience encoding, and adaptive behavior.

---

## 💡 Example Use Case

```csharp
var memory = new WorkingMemoryStore("working.db", TimeSpan.FromMinutes(30));
await memory.StoreNodeAsync(new MemoryNode {
    Id = Guid.NewGuid(),
    Label = "stimulus:file:report.docx",
    Embedding = Vector.FromText("financial document"),
    EmotionalCharge = 0.4f
});

var relevant = await memory.QueryRelevantNodesAsync(queryEmbedding, new DefaultRelevanceScorer());
foreach (var match in relevant)
{
    Console.WriteLine($"Found related: {match.Node.Label} with weight {match.FinalWeight}");
}
```

---

## 🔍 Original LiteDB Features Preserved

Everything great about LiteDB still applies:
- Single-file NoSQL embedded storage
- Full ACID transactions
- LINQ and SQL-like queries
- BSON document mapping and POCO support
- Thread-safe and compact (~450KB)

---

## 🧬 Project License

Synthetik.LiteDB.MemoryEngine inherits the [MIT License](http://opensource.org/licenses/MIT) from LiteDB. All cognitive extensions are offered under the same license, with attribution.

---

## 📚 Learn More

- [Synthetik Delusion Framework Overview](https://github.com/hurley451/synthetikdelusion)
- [LiteDB Original Project](https://github.com/mbdavid/LiteDB)
- [LiteDB Studio UI](https://github.com/mbdavid/LiteDB.Studio)

