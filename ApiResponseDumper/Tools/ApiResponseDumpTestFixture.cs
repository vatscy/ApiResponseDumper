using Newtonsoft.Json.Serialization;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Text;
using System.Web.Http;

namespace ApiResponseDumper.Tools
{
	/// <summary>
	/// Controllerのテストで使用したテストデータを
	/// クライアント側のテストにも流用できるように
	/// JavaScriptの変数として保存するクラス。
	///  </summary>
	public class ApiResponseDumpTestFixture
	{
		private const string DirectoryPath = @"api-response-dump";
		private const string PartsDirectoryPath = DirectoryPath + @"\apitest-parts";
		private const string MasterDirectoryPath = DirectoryPath + @"\apitest";
		private const string TestHeaderFileName = "test-header.txt";
		private const string TestFooterFileName = "test-footer.txt";

		private static bool _hasInitialized;

		/// <summary>
		/// テスト開始時に
		/// 過去の断片ファイルを全て削除する。
		/// </summary>
		[TestFixtureSetUp]
		public void TestFixtureSetUp()
		{
			if (_hasInitialized)
			{
				return;
			}
			CreateHeaderAndFooter();
			RemoveAllFragments();
			_hasInitialized = true;
		}

		/// <summary>
		/// テスト終了時に
		/// 断片ファイルを結合してファイル化する。
		/// </summary>
		[TestFixtureTearDown]
		public void TestFixtureTearDown()
		{
			CreateMasterJsFile();
		}

		/// <summary>
		/// Controllerのテストで使用するテストデータをJSONに変換し
		/// JavaScriptの変数に割り当てるテキストを書き
		/// ファイルとして保存する。
		/// HttpResponseMessage型を返す場合に使用。
		/// </summary>
		/// <param name="expression">Controllerのメソッド呼び出しをラムダ式にしたもの</param>
		/// <returns>(Tuple) Item1: メソッド正常処理時の戻り値 / Item2: HttpResponseException発生時のHttpResponseMessage
		/// どちらか一方のみ格納され、もう一方はnullが入る。</returns>
		protected Tuple<HttpResponseMessage, HttpResponseMessage> SaveControllerResponse(Expression<Func<HttpResponseMessage>> expression)
		{
			string controllerName;
			string methodName;
			string testMethodName;

			var result = AnalyzeExpression(expression, out controllerName, out methodName, out testMethodName);

			// 戻り値(HttpResponseMessage)からContent-Valueを抽出してそれをJSON化
			var resultValue = result.Item2 == null ? ((ObjectContent)result.Item1.Content).Value : ((ObjectContent)result.Item2.Content).Value;
			CreateFragmentJsFile(controllerName, methodName, testMethodName, resultValue);

			return result;
		}

		/// <summary>
		/// Controllerのテストで使用するテストデータをJSONに変換し
		/// JavaScriptの変数に割り当てるテキストを書き
		/// ファイルとして保存する。
		/// エンティティを返す場合に使用。
		/// </summary>
		/// <typeparam name="T">Controllerのメソッドの戻り値の型</typeparam>
		/// <param name="expression">Controllerのメソッド呼び出しをラムダ式にしたもの</param>
		/// <returns>(Tuple) Item1: メソッド正常処理時の戻り値 / Item2: HttpResponseException発生時のHttpResponseMessage
		/// どちらか一方のみ格納され、もう一方はその型のデフォルト値が入る。</returns>
		protected Tuple<T, HttpResponseMessage> SaveControllerResponse<T>(Expression<Func<T>> expression)
		{
			string controllerName;
			string methodName;
			string testMethodName;

			var result = AnalyzeExpression(expression, out controllerName, out methodName, out testMethodName);
			if (result.Item2 == null)
			{
				// success: 戻り値をそのままJSON化
				CreateFragmentJsFile(controllerName, methodName, testMethodName, result.Item1);
			}
			else
			{
				// failure: 戻り値(HttpResponseMessage)からContent-Valueを抽出してそれをJSON化
				var resultValue = ((ObjectContent)result.Item2.Content).Value;
				CreateFragmentJsFile(controllerName, methodName, testMethodName, resultValue);
			}

			return result;
		}

		/// <summary>
		/// メソッドを解析し
		/// コントローラ名、メソッド名、テストメソッド名、戻り値を割り出す。
		/// </summary>
		/// <typeparam name="T">Controllerのメソッドの戻り値の型</typeparam>
		/// <param name="expression">Controllerのメソッド呼び出しをラムダ式にしたもの</param>
		/// <param name="controllerName">コントローラ名</param>
		/// <param name="methodName">メソッド名</param>
		/// <param name="testMethodName">テストメソッド名</param>
		/// <returns>(Tuple) Item1: メソッド正常処理時の戻り値 / Item2: HttpResponseException発生時のHttpResponseMessage
		/// どちらか一方のみ格納され、もう一方はその型のデフォルト値が入る。</returns>
		private static Tuple<T, HttpResponseMessage> AnalyzeExpression<T>(Expression<Func<T>> expression, out string controllerName, out string methodName, out string testMethodName)
		{
			// ラムダ式を取得
			var lambda = expression as LambdaExpression;
			if (lambda == null)
			{
				throw new ArgumentException("expression");
			}

			// ラムダ式からメソッドを抽出
			MethodCallExpression methodExpr = null;
			if (lambda.Body.NodeType == ExpressionType.Call)
			{
				methodExpr = lambda.Body as MethodCallExpression;
			}
			if (methodExpr == null)
			{
				throw new ArgumentException("expression");
			}

			// メソッドの各情報を取得
			var methodInfo = methodExpr.Method;
			var controllerFullName = methodExpr.Method.DeclaringType.FullName;
			controllerName = controllerFullName.Split('.').Last();
			if (!controllerName.Substring(controllerName.Length - 10).Equals("Controller"))
			{
				throw new Exception("コントローラの名前が不適切です");
			}
			methodName = methodInfo.Name;
			testMethodName = new StackFrame(2).GetMethod().Name;

			var success = default(T);
			HttpResponseMessage failure = null;
			try
			{
				success = expression.Compile()();
			}
			catch (HttpResponseException e)
			{
				failure = e.Response;
			}
			return Tuple.Create(success, failure);
		}

		/// <summary>
		/// テストデータをJSONに変換し
		/// JavaScriptの変数に割り当てるテキストを書き
		/// ファイルとして保存する。
		/// </summary>
		/// <typeparam name="T">Controllerのメソッドの戻り値の型</typeparam>
		/// <param name="controllerName">コントローラ名</param>
		/// <param name="methodName">メソッド名</param>
		/// <param name="testMethodName">テストメソッド名</param>
		/// <param name="result">Controllerのメソッドを実行した際の戻り値</param>
		private static void CreateFragmentJsFile<T>(string controllerName, string methodName, string testMethodName, T result)
		{
			// resultをJSON化
			var jsonFormatter = GlobalConfiguration.Configuration.Formatters.JsonFormatter;
			jsonFormatter.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
			Stream stream = new MemoryStream();
			var content = new StreamContent(stream);
			jsonFormatter.WriteToStreamAsync(result.GetType(), result, stream, content, null).Wait();
			stream.Position = 0;
			var json = content.ReadAsStringAsync().Result;

			// シングルクォーテーションをエスケープ
			json = json.Replace("\'", "\\\'");

			// 保存するディレクトリを作成
			if (!Directory.Exists(PartsDirectoryPath))
			{
				Directory.CreateDirectory(PartsDirectoryPath);
			}

			var controllerNameHead = controllerName.Substring(0, controllerName.Length - 10);

			// 保存するファイル名を決定({ControllerNameHead}-{MethodName}-{TestMethodName}.part.txt)
			var fileName = string.Format(@"{0}\{1}-{2}-{3}.part.txt", PartsDirectoryPath, controllerNameHead, methodName, testMethodName);

			// JavaScript形式の文字列を作成しファイルに書き込み
			var contents = new StringBuilder();
			contents.AppendFormat("testData = testData || {{}};").Append(Environment.NewLine)
				.AppendFormat("testData['{0}'] = testData['{0}'] || {{}};", controllerNameHead).Append(Environment.NewLine)
				.AppendFormat("testData['{0}']['{1}'] = testData['{0}']['{1}'] || {{}};", controllerNameHead, methodName).Append(Environment.NewLine)
				.AppendFormat("testData['{0}']['{1}']['{2}'] = JSON.parse('{3}');", controllerNameHead, methodName, testMethodName, json);
			File.WriteAllText(fileName, contents.ToString());
		}

		/// <summary>
		/// 断片ファイルを結合する際のヘッダとフッタを生成する。
		/// </summary>
		private static void CreateHeaderAndFooter() {
			if (!Directory.Exists(PartsDirectoryPath))
			{
				Directory.CreateDirectory(PartsDirectoryPath);
			}
			const string header = "(function(global){var testData;";
			File.WriteAllText(PartsDirectoryPath + @"\" + TestHeaderFileName, header);
			const string footer = "global.testData = testData;})(window);";
			File.WriteAllText(PartsDirectoryPath + @"\" + TestFooterFileName, footer);
		}

		/// <summary>
		/// SaveControllerResponseで生成した断片ファイルを
		/// 全て削除する。
		/// </summary>
		private static void RemoveAllFragments()
		{
			if (!Directory.Exists(PartsDirectoryPath))
			{
				Directory.CreateDirectory(PartsDirectoryPath);
			}
			foreach (var filePath in Directory.GetFiles(PartsDirectoryPath, "*.part.txt"))
			{
				File.Delete(filePath);
			}
		}

		/// <summary>
		/// SaveControllerResponseで生成した断片ファイルを
		/// ひとつのJavaScriptファイルにまとめる。
		/// </summary>
		private static void CreateMasterJsFile()
		{
			var contents = new StringBuilder();

			// 各断片ファイルの内容を結合
			contents.Append(File.ReadAllText(string.Format(@"{0}\test-header.txt", PartsDirectoryPath))).Append(Environment.NewLine);
			foreach (var filePath in Directory.GetFiles(PartsDirectoryPath, "*.part.txt"))
			{
				contents.Append(File.ReadAllText(filePath)).Append(Environment.NewLine);
			}
			contents.Append(File.ReadAllText(string.Format(@"{0}\test-footer.txt", PartsDirectoryPath))).Append(Environment.NewLine);

			// ファイルに保存
			if (!Directory.Exists(MasterDirectoryPath))
			{
				Directory.CreateDirectory(MasterDirectoryPath);
			}
			File.WriteAllText(MasterDirectoryPath + @"\test-data.js", contents.ToString());
		}
	}
}
