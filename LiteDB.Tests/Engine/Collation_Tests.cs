using FluentAssertions;
using LiteDB.Engine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class Collation_Tests
    {
        [Fact]
        public void Culture_Ordinal_Sort()
        {
            // 1046 = pt-BR
            var collation = new Collation(1046, CompareOptions.IgnoreCase);

            var s = new EngineSettings
            {
                DataStream = new MemoryStream()
            };

            var names = new string[] { "Ze", "Ana", "Ána", "Ánã", "Ana Paula", "ana lucia" };

            var sortByLinq = names.OrderBy(x => x, collation).ToArray();
            var findByLinq = names.Where(x => collation.Compare(x, "ANA") == 0).ToArray();

            using(var e = new LiteEngine(s))
            {
                //e.Rebuild(new RebuildOptions { Collation = collation });

                e.Insert("col1", names.Select(x => new BsonDocument { ["name"] = x }), BsonAutoId.Int32);

                // sort by merge sort
                var sortByOrderByName = e.Query("col1", new Query { OrderBy = "name" })
                    .ToEnumerable()
                    .Select(x => x["name"].AsString)
                    .ToArray();

                var query = new Query();
                query.Where.Add("name = 'ANA'");

                // find by expression
                var findByExpr = e.Query("col1", query)
                    .ToEnumerable()
                    .Select(x => x["name"].AsString)
                    .ToArray();

                sortByOrderByName.Should().BeEquivalentTo(sortByLinq);
                findByExpr.Should().BeEquivalentTo(findByLinq);

                // index test
                e.EnsureIndex("col1", "idx_name", "name", false);

                // sort by index
                var sortByIndexName = e.Query("col1", new Query { OrderBy = "name" })
                    .ToEnumerable()
                    .Select(x => x["name"].AsString)
                    .ToArray();

                // find by index
                var findByIndex = e.Query("col1", query)
                    .ToEnumerable()
                    .Select(x => x["name"].AsString)
                    .ToArray();

                sortByIndexName.Should().BeEquivalentTo(sortByLinq);
                findByIndex.Should().BeEquivalentTo(findByLinq);
            }
        }

        [Fact(Skip = "Must fix in CI - works only in Windows local machine")]
        public void Create_Database_Using_Current_Culture()
        {
            var current = CultureInfo.CurrentCulture;

            CultureInfo.CurrentCulture = new CultureInfo("fi");

            using (var e = new LiteEngine())
            {
                var d = e.Pragma(Pragmas.COLLATION);

                d.AsString.Should().Be("fi/IgnoreCase");
            }

            CultureInfo.CurrentCulture = current;
        }

        [Fact]
        public void Change_Thread_Culture()
        {
            using(var f = new TempFile())
            {
                var current = CultureInfo.CurrentCulture;

                CultureInfo.CurrentCulture = new CultureInfo("fi");

                // store in database using "fi" culture
                using (var e = new LiteEngine(f.Filename))
                {
                    e.Insert("col1", data.Select(x => new BsonDocument { ["_id"] = x }), BsonAutoId.Int32);
                }

                // change current culture do "en-GB"
                CultureInfo.CurrentCulture = new CultureInfo("en-gb");

                using (var e = new LiteEngine(f.Filename))
                {
                    foreach(var id in data)
                    {
                        var doc = e.Find("col1", BsonExpression.Create("_id = @0", id)).Single();

                        doc["_id"].AsString.Should().Be(id);
                    }
                }

                CultureInfo.CurrentCulture = current;
            }
        }

        [Fact]
        public void Collaction_New_Database()
        {
            var s = new EngineSettings
            {
                DataStream = new MemoryStream(),
                Collation = new Collation("en-US/None")
            };

            using (var db = new LiteDatabase(new LiteEngine(s)))
            {
                db.Collation.Culture.Name.Should().Be("en-US");
                db.Collation.SortOptions.Should().Be(CompareOptions.None);
            }
        }

        private readonly string[] data = new string[]
        {
            "r6pfkr.4keQyr", "r6pfjI.31qrGW", "r6pfjy.1ryYCW", "r6pfjs.1iCqiD", "r6pfjm.2xXoUr", "r6pfj9.sYaWO", "r6pfgj.1aguPU", "r6pfgd.kKEyS",
            "r6pfg7.1PD90r", "r6pffZ.1JrB8C", "r6pffU.4Exn9y", "r6pffN.atYDW", "r6pfc1.4cqgs6", "r6pfbF.1YAU7Y", "r6pfbx.463ddU", "r6pfbr.3HzECI",
            "r6pf9r.4ynNR", "r6pf9k.4GtaSC", "r6pf8S.4GyjFo", "r6pf49.335Ko4", "r6pf3V.rKPCX", "r6peZa.3af9h8", "r6peYq.2sDiQt", "r6peXj.1NkX0l",
            "r6peXc.1z8Gke", "r6peX5.mwSkb", "r6peWY.1ERxZg", "r6peWS.30hjll", "r6peR6.3UNjHd", "r6peQd.kxoMi", "r6peMK.dCIVO", "r6peLi.4AEHU3",
            "r6peL2.1qbyCp", "r6peKW.2v9U7X", "r6pevW.ziicH", "r6pevQ.2R9iwj", "r6pevd.3SfYHD", "r6peuv.1ji9F4", "r6pesc.2BXuZt", "r6penL.3pRAK0",
            "r6penD.28lRKL", "r6pekV.4CQNO9", "r6pekP.3N2SgM", "r6peiK.ZCbYg", "r6pefr.4aYnvN", "r6pef8.1xvyOh", "r6pebO.3q4BLp", "r6pebH.1QOhn9",
            "r6pdU8.2CMl0G", "r6pdTr.21C0Jo",  "r6pdTl.1gZkJS", "r6pdSZ.11aUim", "r6pdS8.4ajIuB", "r6pdRz.2rOGSP", "r6pdRs.3U8IIx", "r6pdRe.2TwNN4",
            "r6pdR7.kgbfa", "r6pdPu.1eNFMi", "r6pdO7.3kq0nv", "r6pdO3.29BOsB", "r6pdNX.uS8hE", "r6pdNg.2ucRK5", "r6pdML.R1XyW", "r6pdH3.vgJzk",
            "r6pdGW.222gtT", "r6pdFg.dPgET",  "r6pdD5.3izAKD", "r6pdCV.jkjQL", "r6pdyA.1p23Cc", "r6pdyq.1I9JIJ", "r6pdyg.1fSOFi", "r6pdvo.13PNdE",
            "r6pdvi.1hlgNk", "r6pdu5.1klb90",  "r6pdtZ.X4e7j", "r6pdpd.3JMgtk", "r6pdmM.2Is3UL", "r6pdmH.1nDz9w", "r6pdmB.9rN7V", "r6pdjz.fkNfw",
            "r6pdit.3ruGes", "r6pdfN.43XLD1", "r6pdf0.2Z8i47", "r6pdeT.4czd5C", "r6pdeM.4yDpDL", "r6pd9p.406cIF", "r6pd7v.3xqJEk", "r6pcXN.1EEqIY",
            "r6pcXG.1NDfe4", "r6pcXv.1Yr5D4", "r6pcVz.3q8hEX", "r6pcVt.1RFapt", "r6pcOX.13oday", "r6pcOG.WJfzH", "r6pcOz.1qNZKG", "r6pcLv.4CyFii",
            "r6pcIt.2xFaQc", "r6pcIm.321Qpx", "r6pcEZ.2il0Z9", "r6pcEN.2irJz3", "r6pcty.48iYe5", "r6pcll.20YWd8", "r6pcii.1zgAsv", "r6pcdC.2RDRSN",
            "r6pcb2.LrkxT", "r6pcaq.20bCEh", "r6pc9f.1GmU8z", "r6pc3A.2tpydA", "r6pc1M.3ZISZZ", "r6pc1A.jky3a", "r6pbU2.AZMkB", "r6pbTT.3ENCXm",
            "r6pbTL.3dFiv4", "r6pbTC.47mjqT", "r6pbR0.2tMF28", "r6pbv5.183tiK", "r6pbqi.H04AO", "r6pblA.4jVc6v", "r6pbc5.qU3M1", "r6pbc0.2x9f5u",
            "r6pb8S.4lqxEf", "r6pb8F.3hKJ9f", "r6pb2N.1nv2oa", "r6paXU.2vI8FP", "r6paT3.23Tc9r", "r6paSz.6uVlP", "r6paPM.t26Qg", "r6paJh.4aCf0M","r6paDT.hTI7Z",
            "r6pax9.1Kj7nL", "r6paw6.13iLNH", "r6parj.4mQxY", "r6parc.3yGSF2", "r6paqU.48WiSL", "r6paow.r0t0M", "r6panr.2EIzK4", "r6panc.41QPvW",
            "r6pakd.2xqwi1", "r6pahn.1KDZEH", "r6pagD.40T2zQ", "r6pa0s.UaqJC", "r6p9WU.8SSSe", "r6p9RM.4pS5AB", "r6p9OW.2ko8Tq", "r6p9Jw.2hWpU6",
            "r6p9Fa.3lFxDy", "r6p9Cy.3rqkGv", "r6p9xC.1xrjLm", "r6p9xw.3THBs7", "r6p9p5.FP1JE"
        };
    }
}