﻿namespace ChristianRondeau.JiraTogglSync.Services
{
	public class Issue
	{
		public string Key { get; set; }
		public string Summary { get; set; }

		public override string ToString()
		{
			return string.Format("{0}: {1}", Key, Summary);
		}
	}
}