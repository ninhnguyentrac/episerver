using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Xml;
using Castle.Components.DictionaryAdapter;
using EPiServer;
using EPiServer.Cms.Shell;
using EPiServer.Core;
using EPiServer.Data.Dynamic;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Find.Cms;
using EPiServer.Forms.Core;
using EPiServer.Forms.Core.Data;
using EPiServer.Forms.Core.Models;
using EPiServer.Forms.Implementation.Elements;
using EPiServer.Framework.Blobs;
using EPiServer.PlugIn;
using EPiServer.ServiceLocation;
using EPiServer.Web;
using EPiServer.Web.Routing;
using GrantThornton.Interface.Web.Business.Extensions;
using GrantThornton.Interface.Web.Core.Extensions;
using GrantThornton.Interface.Web.Core.Helper;
using GrantThornton.Interface.Web.Core.Interfaces;
using GrantThornton.Interface.Web.Core.Services;
using GrantThornton.Interface.Web.Models.Base;
using GrantThornton.Interface.Web.Models.Pages;
using GrantThornton.Interface.Web.Models.Properties;
using GrantThornton.Interface.Web.Models.SiteDefinitions;
using GrantThornton.Interface.Web.Models.ViewModels;
using GrantThornton.Interface.Web.Models.ViewModels.Blocks.Location;
using Microsoft.Scripting.Utils;

namespace GrantThornton.Interface.Web.Controllers.Admin
{
	[GuiPlugIn(
		Area = PlugInArea.AdminMenu,
		Url = "/cms/admin/ExportEpiFormSubmissionData/Index",
		DisplayName = "Export Epi Form Submission Data")]
	public class ExportEpiFormSubmissionDataController : Controller
	{
		private readonly IContentTypeRepository _contentTypeRepository;
		private readonly IContentModelUsage _contentModelUsage;
		private readonly TemplateResolver _templateResolver;
		public ExportEpiFormSubmissionDataController(IContentTypeRepository contentTypeRepository, IContentModelUsage contentModelUsage)
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
		public ActionResult DoImport(HttpPostedFileBase file)
		{
			try
			{
				if (file.ContentLength > 0)
				{

					var importSumissionData = new EpiFormDataImport(file.InputStream);
					var isGetSubmissionData = importSumissionData.GetSubmissionDataFromStream();
				}
				ViewBag.Message = "File Uploaded Successfully!!";
				return View();
			}
			catch
			{
				ViewBag.Message = "File upload failed!!";
				return View();
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
		public EpiFormDataImport(Stream input)
		{
			_fileStream = input;
		}

		public bool GetSubmissionDataFromStream()
		{
			try
			{
				BinaryFormatter bformatter = new BinaryFormatter();
				var results = bformatter.Deserialize(_fileStream) as Submission[];
				if (results != null && results.Length > 0)
					return true;
				return false;
			}
			catch (Exception e)
			{
				return false;
			}
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
		private string _formFieldString = "__field_";
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

				foreach (var submission in submissions)
				{

					xmlWriter.WriteStartElement("submission");
					submission.WriteXml(xmlWriter);
					xmlWriter.WriteEndElement();
				}
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
			var formIdentity = new FormIdentity(contentDatas.ContentGuid, contentDatas.LanguageBranch());
			var submissionData = _permanentStorate.LoadSubmissionFromStorage(formIdentity, DateTime.MinValue, DateTime.MaxValue);
			if (submissionData != null)
			{
				var submissionConvert = submissionData.Select(x => new Submission
				{
					Id = contentDatas.ContentGuid.ToString(),
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

		private bool IsSYSTEMCOLUMNHostedPageField(string input)
		{
			return input == EPiServer.Forms.Constants.SYSTEMCOLUMN_HostedPage;
		}

		private bool IsFieldElement(string input)
		{
			return input.Contains(_formFieldString);
		}

	}
}
