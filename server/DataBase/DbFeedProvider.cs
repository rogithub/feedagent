﻿using Message;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Data.SqlClient;
using System.Reflection;
using System.Text;

namespace DataBase
{
	public static class DbFeedProvider
	{
		/// <summary>
		/// https://stackoverflow.com/questions/2133393/is-update-with-nested-select-atomic-operation
		/// </summary>
		static string SELECT_UPDATE = @"
		WITH q AS
        (
			SELECT  TOP {0} *
			FROM    T_FEED_QUEUE WITH (ROWLOCK, READPAST)
			WHERE   F_DATE_STARTED IS NULL
			ORDER BY
					F_DATE_CREATED
        )
		UPDATE  q
		SET	F_INSTANCE_ID = @instanceId, F_DATE_STARTED=GETDATE(), F_MACHINE_NAME = @machineName";

		public static IEnumerable<IAssemblyData> GetNextBatch(int batchSize, string machineName, Guid instanceId)
		{
			string text = string.Format(SELECT_UPDATE, batchSize);

			SqlCommand cmd = Db.GetCommand(text, CommandType.Text);
			cmd.Parameters.Add(Db.GetParam("@instanceId", SqlDbType.UniqueIdentifier, instanceId));
			cmd.Parameters.Add(Db.GetParam("@machineName", SqlDbType.VarChar, machineName));

			List<AssemblyData> list = new List<AssemblyData>();

			Db.ExecuteDataReader(cmd, dr =>
			{
				while (dr.Read())
				{
					list.Add(new AssemblyData()
					{
						Assembly = GetCastedByteArray<Assembly>(dr["F_ASSEMBLY"]),
						MethodParametersTypes = GetCastedByteArray<Type[]>(dr["F_METHOD_PARAM_TYPES"]),
						ConstructorParameters = GetCastedXml<object[]>(dr["F_CONSTRUCTOR_PARAMETERS"]),
						MethodParameters = GetCastedXml<object[]>(dr["F_METHOD_PARAMETERS"]),
						FullyQualifiedName = Conversions.GetString(dr["F_FULLY_QUALIFIED_CLASS_NAME"]),
						MessageId = Conversions.GetCastValue<Guid>(dr["F_GUID"]),
						MethodToRun = Conversions.GetString(dr["F_METHOD_NAME"]),
						TimeoutMilliseconds = Conversions.GetCastValue<int>(dr["F_TIMEOUT_MILLISECONDS"])
					});
				}
			});

			return list;
		}

		public static int Save(IAssemblyData[] rows)
		{
			StringBuilder sb = new StringBuilder();
			List<SqlParameter> allParams = new List<SqlParameter>();
			string text = @"INSERT INTO T_FEED_QUEUE 
				(F_GUID, F_STATUS, F_DATE_CREATED, F_TIMEOUT_MILLISECONDS, F_ASSEMBLY, F_METHOD_PARAM_TYPES, F_CONSTRUCTOR_PARAMETERS, F_METHOD_PARAMETERS, F_FULLY_QUALIFIED_CLASS_NAME, F_METHOD_NAME) 
				VALUES 
				(@guid{0},0,GETDATE(),@timeoutms{0},@assembly{0},@paramTypes{0},@consParams{0},@methodParams{0},@fullyQName{0},@method{0});";
			for (int i = 0; i < rows.Length; i++)
			{
				sb.AppendFormat(text, i);
				allParams.Add(Db.GetParam(string.Format("@guid{0}", i), SqlDbType.UniqueIdentifier, rows[i].MessageId));
				allParams.Add(Db.GetParam(string.Format("@timeoutms{0}", i), SqlDbType.Int, rows[i].TimeoutMilliseconds <= 0 ? -1 : rows[i].TimeoutMilliseconds));
				allParams.Add(Db.GetParam(string.Format("@assembly{0}", i), SqlDbType.Binary, Serializer.Serialize(rows[i].Assembly)));
				allParams.Add(Db.GetParam(string.Format("@paramTypes{0}", i), SqlDbType.Binary, Serializer.Serialize(rows[i].MethodParametersTypes)));
				allParams.Add(Db.GetParam(string.Format("@consParams{0}", i), SqlDbType.Xml, Serializer.XmlSerialize(rows[i].ConstructorParameters)));
				allParams.Add(Db.GetParam(string.Format("@methodParams{0}", i), SqlDbType.Xml, Serializer.XmlSerialize(rows[i].MethodParameters)));
				allParams.Add(Db.GetParam(string.Format("@fullyQName{0}", i), SqlDbType.VarChar, rows[i].FullyQualifiedName));
				allParams.Add(Db.GetParam(string.Format("@method{0}", i), SqlDbType.VarChar, rows[i].MethodToRun));
			}

			SqlCommand cmd = Db.GetCommand(sb.ToString(), CommandType.Text);
			cmd.Parameters.AddRange(allParams.ToArray());
			cmd.Connection = Db.GetConnection();
			return Db.ExecuteNonQuery(cmd);
		}

		public static int Update(FinishResult result)
		{
			List<SqlParameter> allParams = new List<SqlParameter>();
			string text = @"UPDATE T_FEED_QUEUE SET F_DATE_COMPLETED=GETDATE(), F_STATUS=@finishStatus, F_RESULT=@result, F_EXCEPTION=@exception WHERE F_GUID=@id";
			SqlCommand cmd = Db.GetCommand(text, CommandType.Text);

			cmd.Parameters.Add(Db.GetParam("@finishStatus", SqlDbType.Int, result.Status));
			cmd.Parameters.Add(Db.GetParam("@result", SqlDbType.Xml, Serializer.XmlSerialize(result.Result)));
			cmd.Parameters.Add(Db.GetParam("@exception", SqlDbType.VarChar, result.Exception == null ? string.Empty : result.Exception.ToString()));
			cmd.Parameters.Add(Db.GetParam("@id", SqlDbType.UniqueIdentifier, result.MessageId));

			cmd.Connection = Db.GetConnection();
			return Db.ExecuteNonQuery(cmd);
		}

		private static T GetCastedXml<T>(object v)
		{
			string xml = Conversions.GetCastValue<string>(v);
			return Serializer.XmlDeserialize<T>(xml);
		}

		private static T GetCastedByteArray<T>(object v)
		{
			byte[] arr = Conversions.GetCastValue<byte[]>(v);
			return Serializer.Deserialize<T>(arr);
		}
	}
}