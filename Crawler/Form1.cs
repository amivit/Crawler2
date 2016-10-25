﻿using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Validation;
using System.Data.SqlClient;
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
                    Console.WriteLine(e.StackTrace);
                    return;
                    //throw;
                }

                HtmlAgilityPack.HtmlDocument doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(HTML);

                string title = doc.DocumentNode.SelectSingleNode("//title").InnerText;

                using(var ctx = new CrawlerContext()) {
                    currentPage.title = title;
                    ctx.Entry(currentPage).State = EntityState.Modified;
                    ctx.SaveChanges();
                }

                HtmlNodeCollection contentNodeCollection =
                    doc.DocumentNode.SelectNodes("(//h1|//h2|//h3|//h4|//h5|//h6|//p)[text()]");
                if(contentNodeCollection != null) {
                    Console.WriteLine("Found {0} content tags", contentNodeCollection.Count);
                    using(var ctx = new CrawlerContext()) {
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

                    foreach(HtmlNode node in linkNodeCollection) {
                        HtmlAttribute att = node.Attributes["href"];

                        string foundLink = att.Value;
                        string linkText = node.InnerText;

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
                        using(var ctx = new CrawlerContext()) {
                            if(!ctx.Pages.Any(x => x.url == foundLink)) {
                                //try {
                                ctx.Pages.Add(new Page() { url = foundLink.Trim() });
                                ctx.SaveChanges();
                                /*} catch(Exception) {
                                Console.WriteLine("Broken link: " + foundLink);
                                continue;
                            }*/
                            }

                            foundPage = ctx.Pages.First(x => x.url == foundLink);
                        }
                        /*try {
                            foundPage = ctx.Pages.Single(x => x.url == foundLink);
                            Console.WriteLine("hurr");
                            break;
                        } catch(Exception) {
                            try {
                                //foundPage = new Page() { url = foundLink.Trim() };

                                //ctx.Pages.Add(foundPage);
                                ctx.Pages.Add(new Page() { url = foundLink.Trim() });
                                ctx.SaveChanges();
                                //foundPage = ctx.Pages.Last();
                            } catch(Exception) {
                                Console.WriteLine("Broken link: " + foundLink);
                                continue;
                            }
                        }*/

                        /*Page foundPage = new Page() { url = foundLink };
                        if(!ctx.Pages.Any(x => x.url == foundLink)) {
                            //linkList.Add(new Page() { url = foundLink });
                            ctx.Pages.Add(foundPage);
                            ctx.SaveChanges();
                            //Console.WriteLine("Found Page: " + foundLink);
                        } else {
                            foundPage = ctx.Pages.First(x => x.url == foundLink);
                            //ctx.Entry(foundPage).GetDatabaseValues();
                        }*/
                        using(var ctx = new CrawlerContext()) {
                            ctx.Links.Add(new Link() {
                                text = linkText.Trim(),
                                local = internalLink,
                                from_id = currentPage.id,
                                to_id = foundPage.id,
                            });

                            try {
                                ctx.SaveChanges();
                            } catch(Exception e) {
                                //MessageBox.Show(e.StackTrace);
                                Console.WriteLine("LinkText: {0} \nlocal: {1} \nfrom_id: {2} \nto_id: {3}",
                                    linkText.Trim(), internalLink, currentPage.id, foundPage.id);
                            }
                        }
                    }
                    //ctx.Pages.AddRange(linkList);

                    //ctx.SaveChanges();
                }
            }
        }

        private void crawlLoop() {
            using(var ctx = new CrawlerContext()) {
                //using(var dbContextTransaction = ctx.Database.BeginTransaction()) {
                try {
                    while(this.running) {
                        try {
                            using(var scope = new TransactionScope(TransactionScopeOption.Required,
                                new TransactionOptions() { IsolationLevel = IsolationLevel.ReadCommitted })) {
                                Page page = ctx.Pages.First(x => x.scanned == false);
                                if(page != null) {
                                    Console.WriteLine("Scanning Page: " + page.url);

                                    this.crawlUrl(page);

                                    page.scanned = true;
                                    ctx.Entry(page).State = EntityState.Modified;
                                    ctx.SaveChanges();
                                    scope.Complete();
                                } else {
                                    Thread.Sleep(1000);
                                    Console.WriteLine("No more links to scan.");
                                }
                            }
                        } catch(Exception e) {
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
            using(var ctx = new CrawlerContext()) {
                ctx.Pages.Add(new Page() { url = "https://en.wikipedia.org/wiki/Main_Page" });
                //ctx.Pages.Add(new Page() { url = "https://en.wikipedia.org/wiki/Wikipedia:Non-free_content_criteria" });
                ctx.SaveChanges();
            }

            //this.linkList.Add("http://stackoverflow.com/");
            this.Start();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e) {
            this.running = false;
        }
    }
}