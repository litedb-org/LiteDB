//using System.IO;
//using FluentAssertions;
//using Xunit;

//namespace LiteDB.Tests.Database
//{
//    public class DbRef_Interface_Tests
//    {
//        #region Model

//        public class UserConfigObject : IUserConfig
//        {
//            [BsonId]
//            public int ID { get; set; }
//            [BsonRef("userInfo")]
//            public IUserInfo UserInfo { get; set; }
//            public string AssignedJob { get; set; }
//            public string[] QueueItems { get; set; }
//        }

//        public class UserInfoObject : IUserInfo
//        {
//            [BsonId]
//            public int ID { get; set; }
//            public string Name { get; set; }
//            public string Username { get; set; }
//            public string IPAddress { get; set; }
//            [BsonRef("userConfig")]
//            public IUserConfig UserConfig { get; set; }
//        }

//        public interface IUserInfo
//        {
//        }

//        public interface IUserConfig
//        {
//        }

//        #endregion

//        [Fact]
//        public void DbRef_With_Interface()
//        {

//            using (var db = new LiteDatabase(new MemoryStream()))
//            {
//                var uc = db.GetCollection<IUserConfig>("userConfig");
//                var ui = db.GetCollection<IUserInfo>("userInfo");

//                var user = new UserInfoObject
//                {
//                    ID = 1,
//                    UserConfig = new UserConfigObject()
//                    {
//                        ID = 99,
//                        AssignedJob = "manager"
//                    }
//                };

//                // let's insert and serialize
//                uc.Insert(user.UserConfig);
//                ui.Insert(user);

//                // let's find (and deserialize)
//                var userConfig = uc.FindById(99);
//                var userInfo = ui.FindById(1);

//                userConfig.Should().NotBeNull();
//                userInfo.Should().NotBeNull();
//            }
//        }
//    }
//}