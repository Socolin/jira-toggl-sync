﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atlassian.Jira;
using Microsoft.Extensions.Options;
using RestSharp;

namespace JiraTogglSync.Services;

public interface IJiraRepository
{
	Task<ICollection<WorkLogEntry>> GetWorkLogOfIssuesAsync(DateTimeOffset fromDate, DateTimeOffset toDate, ICollection<string> issueKeys);
	Task<OperationResult> AddWorkLogAsync(WorkLogEntry entry);
	Task<OperationResult> UpdateWorkLogAsync(WorkLogEntry entry);
	Task<OperationResult> DeleteWorkLogAsync(WorkLogEntry entry);
	Task<JiraUser> GetUserInformation();
}

public class JiraRestService(
	IOptions<JiraRestService.Options> options
) : IJiraRepository
{
	public class Options
	{
		[Required]
		public string Instance { get; set; } = null!;

		[Required]
		public string Username { get; set; } = null!;

		[Required]
		public string ApiToken { get; set; } = null!;
	}

	private readonly Jira _jira = Jira.CreateRestClient(
		options.Value.Instance,
		options.Value.Username,
		options.Value.ApiToken
	);

	public async Task<ICollection<WorkLogEntry>> GetWorkLogOfIssuesAsync(DateTimeOffset fromDate, DateTimeOffset toDate, ICollection<string> issueKeys)
	{
		// Basically we need to find all work log items that have been created or edited within start and end dates.
		// Note: since we don't have endpoint for 'Get ids of worklogs modified since', we will find those work logs
		// through issues that were recently modified.

		if (!issueKeys.Any())
			return Array.Empty<WorkLogEntry>();

		var issues = await _jira.Issues.GetIssuesAsync(issueKeys);
		var workLogs = new ConcurrentBag<WorkLogEntry>();
		await Parallel.ForEachAsync(issues,
			async (issue, ct) =>
			{
				var issueWorkLogs = await GetWorkLogEntriesAsync(fromDate, toDate, issue.Value, ct);
				foreach (var workLog in issueWorkLogs)
					workLogs.Add(workLog);
			}
		);
		return workLogs.OrderBy(x => x.JiraWorkLog.StartDate).ToArray();
	}

	private async Task<List<WorkLogEntry>> GetWorkLogEntriesAsync(
		DateTimeOffset startDate,
		DateTimeOffset endDate,
		Issue issue,
		CancellationToken cancellationToken = default
	)
	{
		var workLogs = await issue.GetWorklogsAsync(cancellationToken);

		return workLogs
			.Where(workLog => workLog.StartDate >= startDate
			                  && workLog.StartDate.Value.AddSeconds(workLog.TimeSpentInSeconds) <= endDate
			                  && (workLog.Author == options.Value.Username || workLog.AuthorUser.Email == options.Value.Username)
			)
			.Select(wl =>
				{
					var sourceId = WorkLogEntry.GetSourceId(wl.Comment);
					return sourceId == null ? null : new WorkLogEntry(issue.Key.Value, sourceId, wl);
				}
			)
			.WhereNotNull()
			.ToList();
	}

	public async Task<OperationResult> UpdateWorkLogAsync(WorkLogEntry entry)
	{
		try
		{
			// https://bitbucket.org/farmas/atlassian.net-sdk/issues/304/update-of-a-worklog
			// https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issue-worklogs/#api-rest-api-3-issue-issueidorkey-worklog-id-put
			await _jira.RestClient.ExecuteRequestAsync(Method.PUT, $"rest/api/3/issue/{entry.IssueKey}/worklog/{entry.JiraWorkLog.Id}", entry.JiraWorkLog);
			return OperationResult.Success(entry);
		}
		catch (Exception ex)
		{
			return OperationResult.Error(ex.Message, entry);
		}
	}

	public async Task<OperationResult> DeleteWorkLogAsync(WorkLogEntry entry)
	{
		try
		{
			await _jira.Issues.DeleteWorklogAsync(entry.IssueKey, entry.JiraWorkLog.Id);
			return OperationResult.Success(entry);
		}
		catch (Exception ex)
		{
			return OperationResult.Error(ex.Message, entry);
		}
	}

	public async Task<OperationResult> AddWorkLogAsync(WorkLogEntry entry)
	{
		try
		{
			await _jira.Issues.AddWorklogAsync(entry.IssueKey, entry.JiraWorkLog);
			return OperationResult.Success(entry);
		}
		catch (Exception ex)
		{
			return OperationResult.Error(ex.Message, entry);
		}
	}

	public async Task<JiraUser> GetUserInformation()
	{
		return await _jira.Users.GetMyselfAsync();
	}
}