﻿using System.ComponentModel.DataAnnotations;

namespace JournalApp;

public class JournalEntry
{
    [Key]
    public DateTime Date { get; set; }
}