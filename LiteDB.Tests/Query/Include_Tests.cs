//using Microsoft.VisualStudio.TestTools.UnitTesting;
//using System;
//using System.IO;
//using System.Linq;
//using System.Threading.Tasks;
//using System.Collections;
//using System.Collections.Generic;
//using System.Text;
//using System.Reflection;
//using System.Text.RegularExpressions;
//using LiteDB.Engine;

//namespace LiteDB.Tests.QueryTest
//{
//    [TestClass]
//    public class Include_Tests
//    {
//        private LiteEngine db;

//        [TestInitialize]
//        public void Init()
//        {
//            db = new LiteEngine();

//            #region Initial Data

//            var sql =
//@"insert into city values { _id: 1, name: 'Porto Alegre', state: 'RS' };
//insert into city values { _id: 2, name: 'Pelotas', state: 'RS' };
//insert into city values { _id: 3, name: 'São Paulo', state: 'SP' };
//insert into city values { _id: 4, name: 'Rio de Janeiro', state: 'RJ' };
//insert into city values { _id: 5, name: 'Niteroi', state: 'RJ' };

//insert into address values {_id: 1, street: 'Ipiranga', nr: 6609, city: {$id: 1, $ref: 'city'} };
//insert into address values {_id: 2, street: 'Protasio', nr: 25, city: {$id: 1, $ref: 'city'} };
//insert into address values {_id: 3, street: 'Brigadeiro Faria Lima', nr: 1100, city: {$id: 3, $ref: 'city'} };
//insert into address values {_id: 4, street: 'Copacabana', nr: 1, cit: {$id: 4, $ref: 'city'} };
//insert into address values {_id: 5, street: 'Travessa do Ouvidor', nr: 225, city: {$id: 5, $ref: 'city'} };

//insert into customer values {_id: 1, name: 'Mauricio', address: {$id: 1, $ref:'address'}};
//insert into customer values {_id: 2, name: 'Carlos', address: {$id: 1, $ref:'address'}};
//insert into customer values {_id: 3, name: 'Joao', address: {$id: 3, $ref:'address'}};
//insert into customer values {_id: 4, name: 'Juliano', address: {$id: 3, $ref:'address'}};
//insert into customer values {_id: 5, name: 'Mauro', address: {$id: 3, $ref:'address'}};
//insert into customer values {_id: 6, name: 'Moacir', address: {$id: 4, $ref:'address'}};
//insert into customer values {_id: 7, name: 'Neimar', address: {$id: 5, $ref:'address'}};";

//            #endregion

//            using (var r = db.Execute(sql))
//            {
//                while (r.NextResult()) ;
//            }
//        }

//        [TestCleanup]
//        public void CleanUp()
//        {
//            db.Dispose();
//        }

//        [Fact]
//        public void Query_Include_Address()
//        {
//            var json = "[{'_id':1,'name':'Mauricio','address':{'_id':1,'street':'Ipiranga','nr':6609,'city':{'$id':1,'$ref':'city'}}},{'_id':2,'name':'Carlos','address':{'_id':1,'street':'Ipiranga','nr':6609,'city':{'$id':1,'$ref':'city'}}},{'_id':3,'name':'Joao','address':{'_id':3,'street':'Brigadeiro Faria Lima','nr':1100,'city':{'$id':3,'$ref':'city'}}},{'_id':4,'name':'Juliano','address':{'_id':3,'street':'Brigadeiro Faria Lima','nr':1100,'city':{'$id':3,'$ref':'city'}}},{'_id':5,'name':'Mauro','address':{'_id':3,'street':'Brigadeiro Faria Lima','nr':1100,'city':{'$id':3,'$ref':'city'}}},{'_id':6,'name':'Moacir','address':{'_id':4,'street':'Copacabana','nr':1,'cit':{'$id':4,'$ref':'city'}}},{'_id':7,'name':'Neimar','address':{'_id':5,'street':'Travessa do Ouvidor','nr':225,'city':{'$id':5,'$ref':'city'}}}]";
//            var r0 = JsonSerializer.Deserialize(json);

//            var r1 = db.Query("customer")
//                .Include("address")
//                .ToArray()
//                .ToBsonArray();

//            Assert.Equal(r0, r1);
//        }

//        [Fact]
//        public void Query_Include_Address_And_City()
//        {
//            var json = "[{'_id':1,'name':'Mauricio','address':{'_id':1,'street':'Ipiranga','nr':6609,'city':{'_id':1,'name':'Porto Alegre','state':'RS'}}},{'_id':2,'name':'Carlos','address':{'_id':1,'street':'Ipiranga','nr':6609,'city':{'_id':1,'name':'Porto Alegre','state':'RS'}}},{'_id':3,'name':'Joao','address':{'_id':3,'street':'Brigadeiro Faria Lima','nr':1100,'city':{'_id':3,'name':'S\u00e3o Paulo','state':'SP'}}},{'_id':4,'name':'Juliano','address':{'_id':3,'street':'Brigadeiro Faria Lima','nr':1100,'city':{'_id':3,'name':'S\u00e3o Paulo','state':'SP'}}},{'_id':5,'name':'Mauro','address':{'_id':3,'street':'Brigadeiro Faria Lima','nr':1100,'city':{'_id':3,'name':'S\u00e3o Paulo','state':'SP'}}},{'_id':6,'name':'Moacir','address':{'_id':4,'street':'Copacabana','nr':1,'cit':{'$id':4,'$ref':'city'}}},{'_id':7,'name':'Neimar','address':{'_id':5,'street':'Travessa do Ouvidor','nr':225,'city':{'_id':5,'name':'Niteroi','state':'RJ'}}}]";
//            var r0 = JsonSerializer.Deserialize(json);

//            var r1 = db.Query("customer")
//                .Include("address")
//                .Include("address.city")
//                .ToArray()
//                .ToBsonArray();

//            Assert.Equal(r0, r1);
//        }
//    }
//}

