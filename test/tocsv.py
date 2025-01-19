from datasets import load_dataset
import pandas as pd

# Load the dataset
dataset = load_dataset('mrm8488/fake-news')

# Access the training split
train_dataset = dataset['train']

# Convert to Pandas DataFrame
df = train_dataset.to_pandas()

# Save to CSV
csv_file_path = './fakenews.csv'
df.to_csv(csv_file_path, index=False)

print(f"CSV file saved to: {csv_file_path}")