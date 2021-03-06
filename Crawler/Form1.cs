﻿using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Validation;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Contexts;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using CrawlerLibrary.Models;
using IsolationLevel = System.Transactions.IsolationLevel;

namespace Crawler {

    public partial class Form1 : Form {

        public Form1() {
            InitializeComponent();
            //hello
        }

        private List<string> linkList = new List<string>();
        private Thread crawlerThread;
        private Thread scannerThread;
        private Thread indexerThread;

        public void Start() {
            this.crawlerThread = new Thread(this.crawlLoop);
            this.crawlerThread.Start();
            /*this.scannerThread = new Thread(this.scan);
            this.scannerThread.Start();
            this.indexerThread = new Thread(this.crawlLoop);
            this.indexerThread.Start();*/
        }

        private bool running = true;

        private void scan() {
            while(this.running) {
            }
        }

        private readonly List<string> scannedLinks = new List<string>();

        private void crawlUrl(Page currentPage) {
            using(var client = new WebClient()) {
                Uri uri = new Uri(currentPage.url);
                string HTML;
                try {
                    HTML = client.DownloadString(uri);
                } catch(WebException e) {
                    //Console.WriteLine(e.StackTrace);
                    return;
                    //throw;
                }

                HtmlAgilityPack.HtmlDocument doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(HTML);

                string title = doc.DocumentNode.SelectSingleNode("//title").InnerText;

                using(var ctx = new CrawlerContext()) {
                    ctx.Pages.Attach(currentPage);
                    currentPage.title = title;
                    //ctx.Entry(currentPage).State = EntityState.Modified;
                    ctx.SaveChanges();
                }

                HtmlNodeCollection contentNodeCollection =
                    doc.DocumentNode.SelectNodes("(//h1|//h2|//h3|//h4|//h5|//h6|//p)[text()]");
                if(contentNodeCollection != null) {
                    Console.WriteLine("Found content tags: \t{0}", contentNodeCollection.Count);
                    using(var ctx = new CrawlerContext()) {
                        ctx.Configuration.AutoDetectChangesEnabled = false;

                        foreach(HtmlNode node in contentNodeCollection) {
                            string content = node.InnerText.Trim();

                            //Console.WriteLine("Found {0} tag", node.OriginalName.Trim());

                            if(content.Length > 0)
                                ctx.Content.Add(new Content() {
                                    page_id = currentPage.id,
                                    tag = node.OriginalName.Trim(),
                                    text = content.Trim()
                                });
                        }
                        ctx.SaveChanges();
                    }
                }

                HtmlNodeCollection linkNodeCollection = doc.DocumentNode.SelectNodes("//a[@href and text()]");
                if(linkNodeCollection != null) {
                    Console.WriteLine("Found {0} links", linkNodeCollection.Count);

                    List<Page> linkList = new List<Page>();

                    CrawlerContext ctx = new CrawlerContext();
                    ctx.Configuration.AutoDetectChangesEnabled = false;

                    int i = 1;
                    BenchMarker BM = new BenchMarker(100);
                    int entitySaveCount = 50;
                    foreach(HtmlNode node in linkNodeCollection) {
                        HtmlAttribute att = node.Attributes["href"];

                        string foundLink = att.Value;
                        string linkText = node.InnerText.Trim();

                        if(string.IsNullOrEmpty(linkText))
                            continue;

                        bool internalLink = false;

                        if(foundLink.StartsWith("//")) {
                            foundLink = uri.Scheme + "://" + foundLink.Substring(2);
                        } else if(foundLink.StartsWith("/")) {
                            // is internal
                            internalLink = true;
                            foundLink = uri.GetLeftPart(UriPartial.Authority) + foundLink;
                        } else if(foundLink.StartsWith("?")) {
                            // is internal
                            internalLink = true;
                            foundLink = uri.GetLeftPart(UriPartial.Path) + foundLink;
                        } else {
                            continue;
                        }

                        /*if (linkList.Contains(foundLink) || scannedLinks.Contains(foundLink))
                        {
                            continue;
                        }

                        linkList.Add(foundLink);
                        Console.WriteLine("Found Page: " + foundLink);
                        */
                        //Console.WriteLine("Found Link: " + foundLink);
                        Page foundPage;

                        Stopwatch stopwatch = new Stopwatch();
                        stopwatch.Start();

                        using(var tctx = new CrawlerContext()) {
                            tctx.Configuration.AutoDetectChangesEnabled = false;
                            try {
                                foundPage = tctx.Pages.First(x => x.url == foundLink);
                            } catch(Exception) {
                                foundPage = new Page() { url = foundLink.Trim() };

                                tctx.Entry(foundPage).State = EntityState.Added;
                                tctx.SaveChanges();
                            }
                        }

                        stopwatch.Stop();

                        long lastScan = stopwatch.ElapsedMilliseconds;
                        BM.Insert(lastScan);

                        ctx.Set<Link>().Add(new Link() {
                            text = linkText,
                            local = internalLink,
                            from_id = currentPage.id,
                            to_id = foundPage.id
                        });

                        /*ctx.Links.Add(new Link() {
                            text = linkText.Trim(),
                            local = internalLink,
                            from_id = currentPage.id,
                            to_id = foundPage.id
                        });*/

                        if(i % entitySaveCount == 0) {
                            ctx.SaveChanges();
                            ctx.Dispose();
                            ctx = new CrawlerContext();
                            ctx.Configuration.AutoDetectChangesEnabled = false;
                        }
                        i++;
                    }
                    
                    Console.WriteLine("Avg link find: \t\t {0}ms", BM.AverageTime);

                    Stopwatch SW = new Stopwatch();
                    SW.Start();
                    if(ctx.ChangeTracker.HasChanges())
                        ctx.SaveChanges();
                    SW.Stop();

                    Console.WriteLine("Savechanges time: \t {0}ms", SW.ElapsedMilliseconds);

                    ctx.Dispose();
                }
                //ctx.Pages.AddRange(linkList);

                //ctx.SaveChanges();
            }
        }

        private void crawlLoop() {
            using(var ctx = new CrawlerContext()) {
                int maxQueueItems = 100;
                Queue<long> timeQueue = new Queue<long>();

                BenchMarker BM = new BenchMarker(100);

                //using(var dbContextTransaction = ctx.Database.BeginTransaction()) {
                try {
                    while(this.running) {
                        try {
                            string query = @"
                                UPDATE TOP (1) Pages
                                SET LastAttempt = GETDATE()
                                OUTPUT
	                                inserted.id,
	                                inserted.url,
	                                inserted.title,
	                                inserted.LastAttempt,
	                                inserted.scanned
                                WHERE
	                                scanned = 0
	                                AND
	                                (DATEDIFF(s, '1970-01-01 00:00:00', LastAttempt) <= DATEDIFF(s, '1970-01-01 00:00:00', GETDATE()) - 60*60 OR LastAttempt IS NULL)";
                            Page page = ctx.Pages.SqlQuery(query).Single();

                            //ctx.Database.ExecuteSqlCommand("BEGIN TRAN");

                            Stopwatch stopwatch = new Stopwatch();
                            stopwatch.Start();

                            //using(var scope = new TransactionScope(TransactionScopeOption.Required,
                            //    new TransactionOptions() { IsolationLevel = IsolationLevel.RepeatableRead })) {
                            using(DbContextTransaction scope = ctx.Database.BeginTransaction()) {
                                //Page page = ctx.Pages.SqlQuery("SELECT TOP 1 * FROM Pages WITH (HOLDLOCK, ROWLOCK) WHERE scanned = 0").Single();
                                //ctx.Database.ExecuteSqlCommand("SELECT TOP 1 * FROM Pages WITH (TABLOCKX, HOLDLOCK) WHERE scanned = 0");

                                //Page page = ctx.Pages.First(x => x.scanned == false);
                                if(page != null) {
                                    Console.WriteLine("Scanning Page: " + page.url);

                                    this.crawlUrl(page);

                                    page.scanned = true;
                                    ctx.Entry(page).State = EntityState.Modified;
                                    ctx.SaveChanges();
                                    //ctx.Database.ExecuteSqlCommand("COMMIT TRAN");
                                    scope.Commit();
                                    //scope.Complete();
                                } else {
                                    Thread.Sleep(1000);
                                    Console.WriteLine("No more links to scan.");
                                }
                            }

                            stopwatch.Stop();

                            long lastScan = stopwatch.ElapsedMilliseconds;
                            BM.Insert(lastScan);

                            /*timeQueue.Enqueue(lastScan);

                            if(timeQueue.Count > maxQueueItems)
                                timeQueue.Dequeue();

                            long averageScanTime = timeQueue.Sum() / timeQueue.Count;*/

                            Console.WriteLine();
                            Console.WriteLine("Last scan took:\t{0} ms.", lastScan);
                            Console.WriteLine("Average scan time:\t{0} ms.", BM.AverageTime);
                            Console.WriteLine();
                        } catch(Exception e) {
                            //ctx.Database.ExecuteSqlCommand("ROLLBACK TRAN");
                            Console.WriteLine(e.Message);
                            Console.WriteLine(e.StackTrace);
                            //throw;
                        }
                    }
                } catch(Exception) { }
                //dbContextTransaction.Commit();
                /*  } catch(Exception e) {
                      dbContextTransaction.Rollback();
                  }*/
                //}
            }

            /*using(var ctx = new CrawlerContext()) {
                while(this.running) {
                    try {
                        Page page = ctx.Pages.First(x => x.scanned == false);
                        if(page != null) {
                            Console.WriteLine("Scanning Page: " + page.url);

                            this.crawlUrl(page);

                            page.scanned = true;
                            ctx.Entry(page).State = EntityState.Modified;
                            ctx.SaveChanges();
                        } else {
                            Thread.Sleep(1000);
                            Console.WriteLine("No more links to scan.");
                        }
                    } catch(Exception e) {
                        Console.WriteLine(e.StackTrace);
                        //throw;
                    }
                }
            }*/
        }

        private void nameBtn_Click(object sender, EventArgs e) {
            /*using(var ctx = new CrawlerContext()) {
                ctx.Pages.Add(new Page() { url = "https://en.wikipedia.org/wiki/Main_Page" });
                //ctx.Pages.Add(new Page() { url = "https://en.wikipedia.org/wiki/Wikipedia:Non-free_content_criteria" });
                ctx.SaveChanges();
            }*/

            //this.linkList.Add("http://stackoverflow.com/");
            this.Start();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e) {
            this.running = false;
        }
    }
}