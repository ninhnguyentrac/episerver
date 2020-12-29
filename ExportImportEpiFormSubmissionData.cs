using Castle.Components.DictionaryAdapter;
using EPiServer;
using EPiServer.Cms.Shell;
using EPiServer.Core;
using EPiServer.Data.Dynamic;
using EPiServer.DataAbstraction;
using EPiServer.Forms.Core;
using EPiServer.Forms.Core.Data;
using EPiServer.Forms.Core.Data.Internal;
using EPiServer.Forms.Core.Models;
using EPiServer.Forms.Implementation.Elements;
using EPiServer.PlugIn;
using EPiServer.ServiceLocation;
using EPiServer.Web;
using GrantThornton.Interface.Web.Business.Extensions;
using GrantThornton.Interface.Web.Core.Extensions;
using GrantThornton.Interface.Web.Models.Base;
using GrantThornton.Interface.Web.Models.SiteDefinitions;
using Microsoft.Scripting.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Xml;

namespace GrantThornton.Interface.Web.Controllers.Admin
{
	[GuiPlugIn(
		Area = PlugInArea.AdminMenu,
		Url = "/cms/admin/ExportImportEpiFormSubmissionData/Index",
		DisplayName = "Export Import Epi Form Submission Data")]
	public class ExportImportEpiFormSubmissionDataController : Controller
	{
		private readonly IContentTypeRepository _contentTypeRepository;
		private readonly IContentModelUsage _contentModelUsage;
		private readonly TemplateResolver _templateResolver;
		public ExportImportEpiFormSubmissionDataController(IContentTypeRepository contentTypeRepository, IContentModelUsage contentModelUsage)
		{
			_contentTypeRepository = contentTypeRepository;
			_contentModelUsage = contentModelUsage;
		}
		// GET: PublishHome
		public ActionResult Index()
		{
			var formContainerBlockType = typeof(FormContainerBlock);
			var formContentTypes = _contentTypeRepository.List().Where(x => x.ModelType != null && (x.ModelType.IsSubclassOf(formContainerBlockType) || x.ModelType == formContainerBlockType)).ToList();

			Dictionary<string, string> contentTypeDictionary = new Dictionary<string, string>();
			foreach (var contentType in formContentTypes)
			{
				contentTypeDictionary.Add(contentType.GUID.ToString(), string.IsNullOrEmpty(contentType.DisplayName) ? contentType.Name.Trim() : contentType.DisplayName.Trim());
			}
			var viewModel = new EpiFormTypeViewModel
			{
				SiteHomePages = ConsolidateContentController.GetSiteHomePageDefinitions(),
				ContentTypeDictionary = contentTypeDictionary
			};
			return View(viewModel);
		}

		[HttpPost]
		public async Task<ActionResult> DoExport(string guidId, int homePageId)
		{
			var contentGuid = new Guid(guidId);
			var exportData = new EpiFormDataExport(contentGuid, homePageId);
			var submissionData = exportData.GetFormSubmissionData();

			return File(submissionData, System.Net.Mime.MediaTypeNames.Text.Plain, $"Home_{homePageId}" + ".dat");
		}

		[HttpPost]
		public async Task<ActionResult> DoExportToXml(string guidId, int homePageId)
		{
			var contentGuid = new Guid(guidId);
			var exportData = new EpiFormDataExport(contentGuid, homePageId);
			var submissionData = exportData.GetFormSubmissionXmlData();

			return File(submissionData, System.Net.Mime.MediaTypeNames.Text.Xml, $"Home_{homePageId}" + ".xml");
		}

		[HttpPost]
		public ActionResult DoImport(HttpPostedFileBase file)
		{
			try
			{
				if (file.ContentLength > 0)
				{

					var importSumissionData = new EpiFormDataImport(file.InputStream);
					var isGetSubmissionData = importSumissionData.GetSubmissionDataFromStream();

					var reports = importSumissionData.DoImport();
					if (reports != null)
					{
						MemoryStream ms = new MemoryStream();
						TextWriter tw = new StreamWriter(ms);
						foreach (var importReport in reports)
						{
							importReport.WriteReport(tw);
						}
						tw.Flush();
						byte[] bytes = ms.ToArray();
						ms.Close();
						Response.Clear();
						Response.ContentType = "application/force-download";
						Response.AddHeader("content-disposition", "attachment;filename=report.txt");
						Response.BinaryWrite(bytes);
						Response.End();
					}
				}
				return RedirectToAction("Index");
			}
			catch
			{
				ViewBag.Message = "File upload failed!!";
				return RedirectToAction("Index");
			}
		}
	}
	public class EpiFormTypeViewModel
	{
		public EpiFormTypeViewModel()
		{
			SiteHomePages = new EditableList<SimpleSiteHomePageDefinitionModel>();
		}
		public Dictionary<string, string> ContentTypeDictionary { get; set; }
		public List<SimpleSiteHomePageDefinitionModel> SiteHomePages { get; set; }
	}

	public class EpiFormDataImport
	{
		private Stream _fileStream;

		private Submission[] _submissions;
		private readonly IContentRepository _contentRepository;
		private readonly IPermanentStorage _permanentStorate;
		private readonly SubmissionStorageFactory _submissionStorageFactoryy;
		public EpiFormDataImport(Stream input)
		{
			_fileStream = input;
			_contentRepository = ServiceLocator.Current.GetInstance<IContentRepository>();
			_permanentStorate = ServiceLocator.Current.GetInstance<IPermanentStorage>();
			_submissionStorageFactoryy = ServiceLocator.Current.GetInstance<SubmissionStorageFactory>();
		}

		public bool GetSubmissionDataFromStream()
		{
			try
			{
				BinaryFormatter bformatter = new BinaryFormatter();
				var results = bformatter.Deserialize(_fileStream) as Submission[];
				if (results != null && results.Length > 0)
				{
					_submissions = results;
					return true;
				}
				return false;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		public IEnumerable<ImportReport> DoImport()
		{
			var reports = new List<ImportReport>();
			var reportItem = new ImportReport();
			reports.Add(reportItem);
			try
			{
				var dataToImports = ConvertToSubmissionImport();
				reportItem.SetInfo(dataToImports);

				foreach (var contentToImport in dataToImports)
				{
					var reportItemSubmission = new ImportReport();
					try
					{
						var formIdentity = new FormIdentity(contentToImport.ContentGuid, contentToImport.Language);
						var submissionStorage = _submissionStorageFactoryy.GetStorage(formIdentity);
						var submissionData = submissionStorage.SaveToStorage(formIdentity, contentToImport.Submission);
						reportItemSubmission.SetSuccessMessage(contentToImport);
					}
					catch (Exception e)
					{
						reportItemSubmission.SetErrorMessage(contentToImport, e.Message);
					}
					reports.Add(reportItemSubmission);
				}
			}
			catch (Exception ex)
			{
				reportItem.SetInfo(ex.Message);
			}
			return reports;
		}


		private IEnumerable<ContentToImport> ConvertToSubmissionImport()
		{
			var contentToImports = _submissions.Select(x => GetContentToImport(x));
			return contentToImports;
		}

		private ContentToImport GetContentToImport(Submission input)
		{
			var arrayString = input.Id.Split(':');
			Guid formContentGuid = Guid.Empty;
			string language = string.Empty;
			if (!string.IsNullOrEmpty(arrayString[1]))
			{
				formContentGuid = new Guid(arrayString[1]);
			}
			if (!string.IsNullOrEmpty(arrayString[0]))
			{
				language = arrayString[0];
			}
			var submission = GetSubmission(input);
			return new ContentToImport
			{
				ContentGuid = formContentGuid,
				Language = language,
				Submission = submission
			};
		}

		private Submission GetSubmission(Submission input)
		{
			return new Submission
			{
				Id = Guid.NewGuid().ToString(),
				Data = ConvertFieldIntToGuid(input.Data)
			};
		}

		private IDictionary<string, object> ConvertFieldIntToGuid(IDictionary<string, object> inputDictionary)
		{

			var newDictionary = new Dictionary<string, object>();
			var lang = string.Empty;
			if (inputDictionary.ContainsKey(EPiServer.Forms.Constants.SYSTEMCOLUMN_Language))
			{
				lang = inputDictionary[EPiServer.Forms.Constants.SYSTEMCOLUMN_Language].ToString();
			}
			foreach (var dicItem in inputDictionary)
			{
				if (EpiFormDataExport.IsFieldElement(dicItem.Key))
				{
					var contentId = GetContentFromField(dicItem.Key, lang);
					if (!string.IsNullOrEmpty(contentId))
					{
						newDictionary.Add($"{EpiFormDataExport._formFieldString}{contentId}", dicItem.Value);
					}
				}
				else if (EpiFormDataExport.IsSYSTEMCOLUMNHostedPageField(dicItem.Key))
				{
					var contentId = GetContentFromField(dicItem.Value.ToString(), lang);
					if (!string.IsNullOrEmpty(contentId))
					{
						newDictionary.Add(EPiServer.Forms.Constants.SYSTEMCOLUMN_HostedPage, contentId);
					}
				}
				else
				{
					newDictionary.Add(dicItem.Key, dicItem.Value);
				}
			}
			return newDictionary;
		}

		private string GetContentFromField(string input, string lang)
		{
			var contentIdString = input.Replace(EpiFormDataExport._formFieldString, string.Empty);
			Guid contentId;
			if (Guid.TryParse(contentIdString, out contentId))
			{
				IContent page;
				if (_contentRepository.TryGet(contentId, new CultureInfo(lang), out page))
				{
					return page.ContentLink.ID.ToString();
				}
			}
			return string.Empty;
		}
	}

	public class EpiFormDataExport
	{
		private readonly IContentTypeRepository _contentTypeRepository;
		private readonly IContentModelUsage _contentModelUsage;
		private readonly IPermanentStorage _permanentStorate;
		private readonly IContentLoader _contentLoader;
		private readonly IContentRepository _contentRepository;
		private Guid _formTypeId;
		private ContentReference _homePage;
		private ContentType _formContentType;
		private int _maxLevelToGet = 5;
		public static string _formFieldString = "__field_";
		public EpiFormDataExport(Guid formTypeId, int homePageId)
		{
			_formTypeId = formTypeId;
			_homePage = new ContentReference(homePageId);
			_contentTypeRepository = ServiceLocator.Current.GetInstance<IContentTypeRepository>();
			_contentModelUsage = ServiceLocator.Current.GetInstance<IContentModelUsage>();
			_permanentStorate = ServiceLocator.Current.GetInstance<IPermanentStorage>();
			_contentLoader = ServiceLocator.Current.GetInstance<IContentLoader>();
			_contentRepository = ServiceLocator.Current.GetInstance<IContentRepository>();
			_formContentType = _contentTypeRepository.Load(_formTypeId);
		}

		public byte[] GetFormSubmissionData()
		{
			var pageDescendants = GetPageDescendants();
			var formContents = GetFormContent(pageDescendants);
			var submissionData = GetSubmissionDatas(formContents);
			var sumissionResult = SerializeSubmission(submissionData.ToArray());
			return sumissionResult;
		}

		public byte[] GetFormSubmissionXmlData()
		{
			var pageDescendants = GetPageDescendants();
			var formContents = GetFormContent(pageDescendants);
			var submissionData = GetSubmissionDatas(formContents);
			var sumissionResult = WriteSubmissionsToXml(submissionData.ToArray());
			return sumissionResult;
		}

		private byte[] SerializeSubmission(Submission[] submissions)
		{
			byte[] fileContent;
			using (var ms = new MemoryStream())
			{
				BinaryFormatter bformatter = new BinaryFormatter();
				bformatter.Serialize(ms, submissions);
				fileContent = ms.GetBuffer();
			}
			return fileContent;
		}

		private byte[] WriteSubmissionsToXml(IEnumerable<Submission> submissions)
		{
			byte[] fileContent;

			XmlWriterSettings settings = new XmlWriterSettings();
			settings.CheckCharacters = false;
			using (var ms = new MemoryStream())
			{
				XmlWriter xmlWriter = XmlWriter.Create(ms, settings);
				xmlWriter.WriteStartElement("submissions");
				foreach (var submission in submissions)
				{

					xmlWriter.WriteStartElement("submission");
					submission.WriteXml(xmlWriter);
					xmlWriter.WriteEndElement();
				}
				xmlWriter.WriteEndElement();
				xmlWriter.Flush();
				fileContent = ms.GetBuffer();
			}
			return fileContent;
		}

		private IEnumerable<ContentReference> GetPageDescendants()
		{
			var descendents = _contentLoader.GetDescendents(_homePage).ToList();
			return descendents;
		}

		private IEnumerable<IContent> GetFormContent(IEnumerable<ContentReference> contentReferences)
		{
			var listContent = new List<IContent>();
			foreach (var contentReference in contentReferences)
			{
				var pageData = _contentLoader.Get<IContent>(contentReference) as PageData;

				if (IsPageHasPublished(pageData))
				{
					var publishedPagesByLanguage = GetPageLanguages(contentReference);
					foreach (var sitePageData in publishedPagesByLanguage)
					{
						var formContentByPage = GetFormContentByPage(sitePageData);
						listContent.AddRange(formContentByPage.Select(x => x as IContent).ToList());
					}
				}

			}
			return listContent;
		}

		private List<BlockData> GetFormContentByPage(SitePageData page)
		{
			var inlineBlocks = page.GetInlineBlockForPage();
			var listFormContent = new List<BlockData>();
			if (inlineBlocks != null && inlineBlocks.Length > 0)
			{
				foreach (var inlineBlock in inlineBlocks)
				{
					GetBlockItems(inlineBlock, listFormContent, page);
				}
			}

			var contentAreaItems = page.GetContentAreaItemsForPage();
			if (contentAreaItems.Length > 0)
			{
				foreach (var contentAreaItem in contentAreaItems)
				{
					if (contentAreaItem.ContentLink.IsNullOrEmpty()) continue;

					IContent content;
					_contentRepository.TryGet(contentAreaItem.ContentLink, page.Language, out content);

					if (content is BlockData)
					{
						if (CheckBlockIsFormType(content as BlockData))
						{
							listFormContent.Add(content as BlockData);
						}
						GetBlockItems((BlockData)content, listFormContent, page);
					}
				}
			}
			return listFormContent;
		}

		private void GetBlockItems(BlockData blockData, List<BlockData> blockDatas, PageData currentPage)
		{
			if (blockData == null) return;

			var secondLevelBlocks = new List<InternalBlockItem>();

			var secondLevelContentAreaItems = blockData.GetContentAreaItemsForBlock();
			var pageLanguage = new CultureInfo(currentPage.LanguageBranch);

			var listBlock = new List<BlockData>();
			foreach (var item in secondLevelContentAreaItems)
			{
				if (item.ContentLink.IsNullOrEmpty()) continue;

				IContent content;

				_contentRepository.TryGet(item.ContentLink, pageLanguage, out content);

				if (content is ElementBlockBase) continue;

				if (content is BlockData)
				{
					listBlock.Add(content as BlockData);

				}
			}
			var inlineBlocks = blockData.GetInlineBlockForBlock();
			if (inlineBlocks != null && inlineBlocks.Length > 0)
				listBlock.AddRange(inlineBlocks);
			if (listBlock != null && listBlock.Any())
			{
				foreach (var inlineBlock in listBlock)
				{
					if (CheckBlockIsFormType(inlineBlock))
					{
						blockDatas.Add(inlineBlock);
					}
					GetBlockItems(inlineBlock as BlockData, blockDatas, currentPage);
				}
			}

		}

		private bool CheckBlockIsFormType(BlockData blockData)
		{
			var modelType = _formContentType.ModelType;

			return blockData != null && (blockData as IContent) != null && (blockData as IContent).ContentTypeID == _formContentType.ID;
		}
		private IEnumerable<SitePageData> GetPageLanguages(ContentReference pageRef)
		{
			var publishedPagesByLanguage = _contentRepository
				.GetLanguageBranches<SitePageData>(pageRef)
				.Where(p => p?.StartPublish != null && p.StartPublish <= DateTime.Now);
			return publishedPagesByLanguage;
		}

		private bool IsPageHasPublished(PageData pageData)
		{
			return pageData != null && pageData.CheckPublishedStatus(PagePublishedStatus.PublishedIgnoreStopPublish);
		}

		private IEnumerable<Submission> GetSubmissionDatas(IEnumerable<IContent> contentDatas)
		{
			var submissionDatas = new List<Submission>();
			foreach (var contentData in contentDatas)
			{
				var submissionItemDatas = GetSubmissionEachContentItem(contentData);
				if (submissionItemDatas != null && submissionItemDatas.Any())
					submissionDatas.AddRange(submissionItemDatas);
			}
			return submissionDatas;
		}

		private IEnumerable<Submission> GetSubmissionEachContentItem(IContent contentDatas)
		{
			var contentLanguage = contentDatas.LanguageBranch();
			var contentGuid = contentDatas.ContentGuid;
			var formIdentity = new FormIdentity(contentGuid, contentLanguage);
			var submissionData = _permanentStorate.LoadSubmissionFromStorage(formIdentity, DateTime.MinValue, DateTime.MaxValue);
			if (submissionData != null)
			{
				var submissionConvert = submissionData.Select(x => new Submission
				{
					Id = $"{contentLanguage}:{contentGuid.ToString()}",
					Data = ConvertFieldIntToGuid(x.ToPropertyBag())
				});
				return submissionConvert;
			}
			return Enumerable.Empty<Submission>();
		}

		private IDictionary<string, object> ConvertFieldIntToGuid(IDictionary<string, object> inputDictionary)
		{

			var newDictionary = new Dictionary<string, object>();
			var lang = string.Empty;
			if (inputDictionary.ContainsKey(EPiServer.Forms.Constants.SYSTEMCOLUMN_Language))
			{
				lang = inputDictionary[EPiServer.Forms.Constants.SYSTEMCOLUMN_Language].ToString();
			}
			foreach (var dicItem in inputDictionary)
			{
				if (IsFieldElement(dicItem.Key))
				{
					var contentGuid = GetContentFromField(dicItem.Key, lang);
					if (!string.IsNullOrEmpty(contentGuid))
					{
						newDictionary.Add($"{_formFieldString}{contentGuid}", dicItem.Value);
					}
				}
				else if (IsSYSTEMCOLUMNHostedPageField(dicItem.Key))
				{
					var contentGuid = GetContentFromField(dicItem.Value.ToString(), lang);
					if (!string.IsNullOrEmpty(contentGuid))
					{
						newDictionary.Add(EPiServer.Forms.Constants.SYSTEMCOLUMN_HostedPage, contentGuid);
					}
				}
				else
				{
					newDictionary.Add(dicItem.Key, dicItem.Value);
				}
			}
			return newDictionary;
		}

		private string GetContentFromField(string input, string lang)
		{
			var contentIdString = input.Replace(_formFieldString, string.Empty);
			int contentId;
			if (int.TryParse(contentIdString, out contentId))
			{
				IContent page;
				if (_contentRepository.TryGet(new ContentReference(contentId), new CultureInfo(lang), out page))
				{
					return page.ContentGuid.ToString();
				}
			}
			return string.Empty;
		}

		public static bool IsSYSTEMCOLUMNHostedPageField(string input)
		{
			return input == EPiServer.Forms.Constants.SYSTEMCOLUMN_HostedPage;
		}

		public static bool IsFieldElement(string input)
		{
			return input.Contains(_formFieldString);
		}

	}

	public class ContentToImport
	{
		public Guid ContentGuid { get; set; }
		public string Language { get; set; }
		public Submission Submission { get; set; }
	}

	public class ImportReport
	{
		public void WriteReport(TextWriter tw)
		{
			if (!string.IsNullOrEmpty(Info))
				tw.WriteLine(Info);
			if (!string.IsNullOrEmpty(Error))
				tw.WriteLine(Error);
			if (!string.IsNullOrEmpty(Success))
				tw.WriteLine(Success);
		}
		public void SetInfo(IEnumerable<ContentToImport> contentToImports)
		{
			Info = $"Total item to import: {contentToImports.Count()}";
		}
		public void SetInfo(string inputString)
		{
			Info = inputString;
		}

		public void SetSuccessMessage(ContentToImport contentToImport)
		{
			Success = $"Success: save success SubmissionId: {contentToImport.Submission.Id}   Formid: {contentToImport.ContentGuid}   Language: {contentToImport.Language}";
		}

		public void SetErrorMessage(ContentToImport contentToImport, string exception = "")
		{
			Error = $"Error: failed to save SubmissionId: {contentToImport.Submission.Id}   Formid: {contentToImport.ContentGuid}   Language: {contentToImport.Language}   Exception: {exception}";
		}
		public string Info { get; set; }
		public string Error { get; set; }
		public string Success { get; set; }

	}
}
