using System;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using LiteDB.Engine;
using System.Collections.Generic;
using Xunit;

namespace LiteDB.Internals
{
    public class BufferWriter_Tests
    {
        [Fact]
        public void Buffer_Write_CString()
        {
            var doc = JsonSerializer.Deserialize("{\"_id\":{\"$numberLong\":\"5\"},\"unique_id\":\"20-133-5\",\"event_log\":[{\"created\":{\"$date\":\"2020-05-06T09:29:10.8350000Z\"},\"type\":\"job_created\"},{\"created\":{\"$date\":\"2020-05-06T09:29:19.0510000Z\"},\"type\":\"asset_added\",\"data\":{\"filename\":[\"IMG_1333.JPG\"],\"filepath\":[\"D:\\\\Users\\\\Daniel\\\\Desktop\\\\German Shepherd\\\\IMG_1333.JPG\"]}},{\"created\":{\"$date\":\"2020-05-06T09:29:23.6910000Z\"},\"type\":\"lookup_preformed\",\"data\":{\"searchterm\":[\"1424101.2\"]}},{\"created\":{\"$date\":\"2020-05-06T09:29:25.9060000Z\"},\"type\":\"lookup_selected\"},{\"created\":{\"$date\":\"2020-05-06T09:29:43.7350000Z\"},\"type\":\"job_saved\"},{\"created\":{\"$date\":\"2020-05-06T09:29:43.7900000Z\"},\"type\":\"job_closed\"},{\"created\":{\"$date\":\"2020-06-10T16:00:30.3950000Z\"},\"type\":\"job_deleted\"},{\"created\":{\"$date\":\"2020-06-10T16:00:30.3950000Z\"},\"type\":\"job_deleted\"},{\"created\":{\"$date\":\"2020-06-10T16:00:30.3950000Z\"},\"type\":\"job_deleted\"},{\"created\":{\"$date\":\"2020-06-10T16:00:30.3950000Z\"},\"type\":\"job_deleted\"}],\"status\":\"PERMANANTDELETE\",\"cleaned_up\":false,\"user_info\":{\"href\":\"/fotoweb/users/dan%40deathstar.local\",\"userName\":\"dan@deathstar.local\",\"fullName\":\"Dan Twomey\",\"firstName\":\"Dan\",\"lastName\":\"Twomey\",\"email\":\"dan@medialogix.co.uk\",\"userId\":\"15003\",\"isGuest\":\"false\",\"userAvatarHref\":\"https://www.gravatar.com/avatar/9496065924d90ffa6b6184c741aa0184?d=mm\"},\"device_info\":{\"_id\":null,\"short_id\":133,\"device_name\":\"DANSCOMPUTER\"},\"template_id\":\"5cb0b82fd1654e07c7a3dd72\",\"created\":{\"$date\":\"2020-05-06T09:29:10.8350000Z\"},\"last_save\":{\"$date\":\"2020-06-15T19:40:50.8250000Z\"},\"files\":[{\"_id\":\"5f9bffbc-a6d7-4ccb-985b-17470745f760\",\"filename\":\"IMG_1333.JPG\",\"extension\":\".JPG\",\"file_checksum\":\"SHA1:09025C2C3009051C51877E052A740140F73EC518\",\"local_file_info\":{\"imported_datetime\":{\"$date\":\"2020-05-06T09:29:17.7650000Z\"},\"system_created_datetime\":{\"$date\":\"2020-03-26T17:04:08.9930000Z\"},\"original_file_path\":\"D:\\\\Users\\\\Daniel\\\\Desktop\\\\German Shepherd\\\\IMG_1333.JPG\",\"local_file_path\":\"C:\\\\ProgramData\\\\Medialogix\\\\Pixel\\\\Upload Storage\\\\20-133-5\\\\5f9bffbc-a6d7-4ccb-985b-17470745f760\\\\IMG_1333.JPG\",\"original_file_directory\":\"D:\\\\Users\\\\Daniel\\\\Desktop\\\\German Shepherd\",\"thumbnail_path\":\"C:\\\\ProgramData\\\\Medialogix\\\\Pixel\\\\Upload Storage\\\\20-133-5\\\\5f9bffbc-a6d7-4ccb-985b-17470745f760\\\\IMG_1333.JPG.thumb\"},\"filesize_bytes\":{\"$numberLong\":\"4225974\"},\"friendly_filesize\":\"4 MB\",\"metadata\":{\"2c0066d2-3f9f-4cf8-8d06-33a544624418\":null,\"4a389ee1-9e1b-4e06-b46f-23f1fd8f6a93\":null,\"b0ad5374-213f-488f-bb21-407e782de287\":null,\"91328cc4-eb72-4c30-9545-e931c830e847\":null,\"b94b21cf-eef3-4e8c-951a-1c20d16d871f\":null,\"3a660b33-c99f-4111-ba88-633533017b40\":null,\"500c2388-ccc1-4b63-8da1-5bbb468a0c5b\":null,\"652cdabe-3c6f-4765-86fd-1680749b412b\":null,\"2a2668c3-2b69-4f9b-89a8-914b70e00aa3\":null,\"fd67fdb2-3705-4f14-a929-5336c8e46489\":null,\"2405d44c-13d3-4ce3-8ba1-dae189139f84\":[],\"8b73f206-8b2c-4ce5-9867-a4e1892370e5\":null,\"5c73f206-8b2c-4ce5-9852-a4e1892370a5\":[\"csitemplate\"],\"9fc32696-4efd-4b6a-8fcc-554c75421cff\":[\"{{asset.uploadtype}}\"],\"c47645ab-0bfa-42e0-9c43-66868f10f90f\":[\"{{curentuser.username}}\"],\"a16a3bae-59bc-4583-9015-7f6bbd0d2b87\":[\"{{job.id}}\"]},\"status\":\"CREATED\",\"file_valid\":false,\"type\":\"IMAGE\",\"fotoweb_responses\":[]}],\"lookup_metadata\":{\"2c0066d2-3f9f-4cf8-8d06-33a544624418\":[\"1424101.2\"],\"4a389ee1-9e1b-4e06-b46f-23f1fd8f6a93\":[\"Exhibit 2\"],\"b0ad5374-213f-488f-bb21-407e782de287\":[\"1424101.2 - Exhibit 2\"],\"91328cc4-eb72-4c30-9545-e931c830e847\":[\"Location 3\"],\"b94b21cf-eef3-4e8c-951a-1c20d16d871f\":[\"DHL\"],\"3a660b33-c99f-4111-ba88-633533017b40\":[\"Medium\"]},\"error_reason\":null,\"retry_count\":0,\"error_counters\":{},\"deleted_datetime\":{\"$date\":\"2020-06-10T16:00:30.3920000Z\"},\"delete_when\":{\"$date\":\"2020-06-15T16:00:30.3920000Z\"}}").AsDocument;
            var size = doc.GetBytesCount(true);

            var arr0 = new BufferSlice(new byte[2935], 0, 2935);
            var arr1 = new BufferSlice(new byte[97], 0, 97);
            var arr2 = new BufferSlice(new byte[5], 0, 5);
            var arr3 = new BufferSlice(new byte[189], 0, 189);

            using (var w = new BufferWriter(new[] { arr0, arr1, arr2, arr3 }))
            {
                w.WriteDocument(doc, false);
                w.Consume();
            }

            using (var r = new BufferReader(new[] { arr0, arr1, arr2, arr3 }))
            {
                var docNew = r.ReadDocument();
            }
        }

        [Fact]
        public void Buffer_Write_CString_Basic()
        {
            var arr = new byte[500];

            var slice0 = new BufferSlice(arr, 0, 3);
            var slice1 = new BufferSlice(arr, 20, 4);
            var slice2 = new BufferSlice(arr, 30, 5);
            var slice3 = new BufferSlice(arr, 40, 6);
            var slice4 = new BufferSlice(arr, 50, 7);

            using (var w = new BufferWriter(new[] { slice0, slice1, slice2, slice3, slice4 }))
            {
                w.WriteCString("123456789*ABCEFGHIJ");
                w.WriteCString("abc");
            }

            using (var r = new BufferReader(new[] { slice0, slice1, slice2, slice3, slice4 }))
            {
                var cstring0 = r.ReadCString();
                var cstring1 = r.ReadCString();
            }
        }


        [Fact]
        public void Buffer_Write_String()
        {
            var source = new BufferSlice(new byte[1000], 0, 1000);

            // direct string into byte[]

            using (var w = new BufferWriter(source))
            {
                w.WriteString("abc123", false);
                w.Position.Should().Be(6);
            }

            Encoding.UTF8.GetString(source.Array, 0, 6).Should().Be("abc123");

            source.Fill(0);

            // BSON string specs

            using (var w = new BufferWriter(source))
            {
                w.WriteString("abc123", true);
            }

            source.ReadInt32(0).Should().Be(7);
            source.ReadString(4, 6).Should().Be("abc123");
            ((char) source.ReadByte(10)).Should().Be('\0');
        }

        [Fact]
        public void Buffer_Write_Numbers()
        {
            var source = new BufferSlice(new byte[1000], 0, 1000);

            // numbers
            using (var w = new BufferWriter(source))
            {
                // max values
                w.Write(int.MaxValue);
                w.Write(uint.MaxValue);
                w.Write(long.MaxValue);
                w.Write(double.MaxValue);
                w.Write(decimal.MaxValue);

                // min values
                w.Write(int.MinValue);
                w.Write(uint.MinValue);
                w.Write(long.MinValue);
                w.Write(double.MinValue);
                w.Write(decimal.MinValue);

                // zero values
                w.Write(0); // int
                w.Write(0u); // uint
                w.Write(0L); // long
                w.Write(0d); // double
                w.Write(0m); // decimal

                // fixed values
                w.Write(1990); // int
                w.Write(1990u); // uint
                w.Write(1990L); // long
                w.Write(1990d); // double
                w.Write(1990m); // decimal
            }

            var p = 0;

            source.ReadInt32(p).Should().Be(int.MaxValue);
            p += 4;
            source.ReadUInt32(p).Should().Be(uint.MaxValue);
            p += 4;
            source.ReadInt64(p).Should().Be(long.MaxValue);
            p += 8;
            source.ReadDouble(p).Should().Be(double.MaxValue);
            p += 8;
            source.ReadDecimal(p).Should().Be(decimal.MaxValue);
            p += 16;

            source.ReadInt32(p).Should().Be(int.MinValue);
            p += 4;
            source.ReadUInt32(p).Should().Be(uint.MinValue);
            p += 4;
            source.ReadInt64(p).Should().Be(long.MinValue);
            p += 8;
            source.ReadDouble(p).Should().Be(double.MinValue);
            p += 8;
            source.ReadDecimal(p).Should().Be(decimal.MinValue);
            p += 16;

            source.ReadInt32(p).Should().Be(0);
            p += 4;
            source.ReadUInt32(p).Should().Be(0u);
            p += 4;
            source.ReadInt64(p).Should().Be(0L);
            p += 8;
            source.ReadDouble(p).Should().Be(0d);
            p += 8;
            source.ReadDecimal(p).Should().Be(0m);
            p += 16;

            source.ReadInt32(p).Should().Be(1990);
            p += 4;
            source.ReadUInt32(p).Should().Be(1990u);
            p += 4;
            source.ReadInt64(p).Should().Be(1990L);
            p += 8;
            source.ReadDouble(p).Should().Be(1990d);
            p += 8;
            source.ReadDecimal(p).Should().Be(1990m);
            p += 16;
        }

        [Fact]
        public void Buffer_Write_Types()
        {
            var source = new BufferSlice(new byte[1000], 0, 1000);

            var g = Guid.NewGuid();
            var d = DateTime.Now;
            var o = ObjectId.NewObjectId();

            using (var w = new BufferWriter(source))
            {
                w.Write(true);
                w.Write(false);
                w.Write(DateTime.MinValue);
                w.Write(DateTime.MaxValue);
                w.Write(d);
                w.Write(Guid.Empty);
                w.Write(g);
                w.Write(ObjectId.Empty);
                w.Write(o);
                w.Write(PageAddress.Empty);
                w.Write(new PageAddress(199, 0));
            }

            var p = 0;

            source.ReadBool(p).Should().BeTrue();
            p += 1;
            source.ReadBool(p).Should().BeFalse();
            p += 1;
            source.ReadDateTime(p).Should().Be(DateTime.MinValue);
            p += 8;
            source.ReadDateTime(p).Should().Be(DateTime.MaxValue);
            p += 8;
            source.ReadDateTime(p).ToLocalTime().Should().Be(d);
            p += 8;
            source.ReadGuid(p).Should().Be(Guid.Empty);
            p += 16;
            source.ReadGuid(p).Should().Be(g);
            p += 16;
            source.ReadObjectId(p).Should().Be(ObjectId.Empty);
            p += 12;
            source.ReadObjectId(p).Should().Be(o);
            p += 12;
            source.ReadPageAddress(p).Should().Be(PageAddress.Empty);
            p += PageAddress.SIZE;
            source.ReadPageAddress(p).Should().Be(new PageAddress(199, 0));
            p += PageAddress.SIZE;
        }

        [Fact]
        public void Buffer_Write_Overflow()
        {
            var data = new byte[50];
            var source = new BufferSlice[]
            {
                new BufferSlice(data, 0, 10),
                new BufferSlice(data, 10, 10),
                new BufferSlice(data, 20, 10),
                new BufferSlice(data, 30, 10),
                new BufferSlice(data, 40, 10)
            };

            using (var w = new BufferWriter(source))
            {
                w.Write(new byte[50].Fill(99, 0, 50), 0, 50);
            }

            data.All(x => x == 99).Should().BeTrue();
        }

        [Fact]
        public void Buffer_Bson()
        {
            var source = new BufferSlice(new byte[1000], 0, 1000);

            var doc = new BsonDocument
            {
                ["minValue"] = BsonValue.MinValue,
                ["null"] = BsonValue.Null,
                ["int"] = int.MaxValue,
                ["long"] = long.MaxValue,
                ["double"] = double.MaxValue,
                ["decimal"] = decimal.MaxValue,
                ["string"] = "String",
                ["document"] = new BsonDocument {["_id"] = 1},
                ["array"] = new BsonArray {1, 2, 3},
                ["binary"] = new byte[50].Fill(255, 0, 49),
                ["objectId"] = ObjectId.NewObjectId(),
                ["guid"] = Guid.NewGuid(),
                ["boolean"] = true,
                ["date"] = DateTime.UtcNow,
                ["maxValue"] = BsonValue.MaxValue
            };

            using (var w = new BufferWriter(source))
            {
                w.WriteDocument(doc, true);

                w.Position.Should().Be(307);
            }

            using (var r = new BufferReader(source, true))
            {
                var reader = r.ReadDocument();

                r.Position.Should().Be(307);

                JsonSerializer.Serialize(reader).Should().Be(JsonSerializer.Serialize(doc));
            }
        }
    }
}