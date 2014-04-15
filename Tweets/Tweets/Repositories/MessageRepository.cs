using System;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing.Design;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using MongoDB.Driver.Linq;
using MongoDB.Driver.Wrappers;
using StructureMap.Query;
using Tweets.ModelBuilding;
using Tweets.Models;

namespace Tweets.Repositories
{
    public class MessageRepository : IMessageRepository
    {
        private readonly IMapper<Message, MessageDocument> messageDocumentMapper;
        private readonly MongoCollection<MessageDocument> messagesCollection;

        public MessageRepository(IMapper<Message, MessageDocument> messageDocumentMapper)
        {
            this.messageDocumentMapper = messageDocumentMapper;
            var connectionString = ConfigurationManager.ConnectionStrings["MongoDb"].ConnectionString;
            var databaseName = MongoUrl.Create(connectionString).DatabaseName;
            messagesCollection =
                new MongoClient(connectionString).GetServer().GetDatabase(databaseName).GetCollection<MessageDocument>(MessageDocument.CollectionName);
        }

        public void Save(Message message)
        {
            var messageDocument = messageDocumentMapper.Map(message);
            messageDocument.Likes = new LikeDocument[0];
            messagesCollection.Insert(messageDocument);
        }

        public void Like(Guid messageId, User user)
        {
            var find = messagesCollection.Find(
                Query.And(Query<MessageDocument>.EQ(m => m.Id, messageId),
                    Query<MessageDocument>.ElemMatch(l => l.Likes, c =>
                        c.EQ(u => u.UserName, user.Name))));
            if (find.Any())
                return;
            var likeDocument = new LikeDocument { UserName = user.Name, CreateDate = DateTime.UtcNow };
            var search = Query<MessageDocument>.EQ(m => m.Id, messageId);
            var updateQ = Update<MessageDocument>.Push(l => l.Likes, likeDocument);
            messagesCollection.Update(search, updateQ);

        }

        public void Dislike(Guid messageId, User user)
        {
            var search = Query<MessageDocument>.EQ(m => m.Id, messageId);
            var updateQ = Update<MessageDocument>.Pull(l => l.Likes,
                 c => c.EQ(u => u.UserName, user.Name));
            messagesCollection.Update(search, updateQ);
        }

        public IEnumerable<Message> GetPopularMessages()
        {
            var or = new BsonDocument()
            {
                {
                    "$or",
                    new BsonArray
                    {
                        new BsonDocument {{"$eq", new BsonArray {"$likes", BsonNull.Value}}},
                        new BsonDocument {{"$eq", new BsonArray {"$likes", new BsonArray()}}}
                    }
                }
            };
            var cond1 = new BsonDocument {{"$cond", new BsonArray {or, new BsonArray {BsonNull.Value}, "$likes"}}};
            var cond2 = new BsonDocument
            {
                {
                    "$cond", new BsonArray
                    {
                        or,
                        new BsonDocument("$add", new BsonArray(new[] {0})),
                        new BsonDocument("$add", new BsonArray(new[] {1}))
                    }
                }
            };
            var project = new BsonDocument
            {
                {
                    "$project",
                    new BsonDocument
                    {
                        {"Id", 1},
                        {"userName", 1},
                        {"text", 1},
                        {"createDate", 1},
                        {"likes", cond1},
                        {"count", cond2}

                    }
                }
            };
            var unwind = new BsonDocument
            {
                {"$unwind", "$likes"}
            };
            var group = new BsonDocument
            {
                {
                    "$group",
                    new BsonDocument
                    {
                        {
                            "_id", new BsonDocument
                            {
                                {"_id", "$_id"},
                                {"userName", "$userName"},
                                {"text", "$text"},
                                {"createDate", "$createDate"}

                            }
                        },
                        {"Likes", new BsonDocument {{"$sum", "$count"}}}

                    }
                }
            };
            var sort = new BsonDocument { { "$sort", new BsonDocument { { "Likes", -1 } } } };
            var limit = new BsonDocument { { "$limit", 10 } };
            var messages = messagesCollection.Aggregate(project, unwind, group, sort, limit).ResultDocuments;
            return (from item in messages
                    let m = item["_id"]
                    let md = BsonSerializer.Deserialize<MessageDocument>((BsonDocument)m)
                    let likes = (int)item["Likes"]
                    select
                        new Message
                        {
                            CreateDate = md.CreateDate,
                            Id = md.Id,
                            Likes = likes,
                            Text = md.Text,
                            User = new User { Name = md.UserName }
                        }).ToArray();

        }

        public IEnumerable<UserMessage> GetMessages(User user)
        {
            var messages =
                messagesCollection.Find(Query<MessageDocument>.EQ(n => n.UserName, user.Name))
                    .SetSortOrder(SortBy<MessageDocument>.Descending(d => d.CreateDate));
            return messages.Select(item => new UserMessage
            {
                Id = item.Id,
                User = new User { Name = item.UserName },
                Text = item.Text,
                CreateDate = item.CreateDate,
                Liked = item.Likes != null && item.Likes.Any(u => u.UserName == user.Name),
                Likes = item.Likes == null ? 0 : item.Likes.Count()
            }).ToArray();
        }
    }
}
