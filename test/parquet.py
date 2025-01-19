from datasets import load_dataset

# Define the local directory to save the dataset
local_cache_dir = "./"

# Download the dataset to the local directory
dataset = load_dataset('argilla/news-fakenews', cache_dir=local_cache_dir)

# Access the training split
train_dataset = dataset['train']

# Print the first example
print(train_dataset[0])

# Convert to Pandas DataFrame for further analysis
import pandas as pd
df = train_dataset.to_pandas()
print(df.head())